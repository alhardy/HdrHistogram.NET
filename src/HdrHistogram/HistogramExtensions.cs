/*
 * This is a .NET port of the original Java version, which was written by
 * Gil Tene as described in
 * https://github.com/HdrHistogram/HdrHistogram
 * and released to the public domain, as explained at
 * http://creativecommons.org/publicdomain/zero/1.0/
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using HdrHistogram.Iteration;
using HdrHistogram.Output;

namespace HdrHistogram
{
    /// <summary>
    /// Extension methods for the Histogram types.
    /// </summary>
    public static class HistogramExtensions
    {

        /// <summary>
        /// Get the highest recorded value level in the histogram
        /// </summary>
        /// <returns>the Max value recorded in the histogram</returns>
        public static long GetMaxValue(this HistogramBase histogram)
        {
            var max = histogram.RecordedValues().Select(hiv => hiv.ValueIteratedTo).LastOrDefault();
            return histogram.HighestEquivalentValue(max);
        }

        /// <summary>
        /// Get the computed mean value of all recorded values in the histogram
        /// </summary>
        /// <returns>the mean value (in value units) of the histogram data</returns>
        public static double GetMean(this HistogramBase histogram)
        {
            var totalValue = histogram.RecordedValues().Select(hiv => hiv.TotalValueToThisValue).LastOrDefault();
            return (totalValue * 1.0) / histogram.TotalCount;
        }

        /// <summary>
        /// Get the computed standard deviation of all recorded values in the histogram
        /// </summary>
        /// <returns>the standard deviation (in value units) of the histogram data</returns>
        public static double GetStdDeviation(this HistogramBase histogram)
        {
            var mean = histogram.GetMean();
            var geometricDeviationTotal = 0.0;
            foreach (var iterationValue in histogram.RecordedValues())
            {
                double deviation = (histogram.MedianEquivalentValue(iterationValue.ValueIteratedTo) * 1.0) - mean;
                geometricDeviationTotal += (deviation * deviation) * iterationValue.CountAddedInThisIterationStep;
            }
            var stdDeviation = Math.Sqrt(geometricDeviationTotal / histogram.TotalCount);
            return stdDeviation;
        }

        /// <summary>
        /// Get the highest value that is equivalent to the given value within the histogram's resolution.
        /// Where "equivalent" means that value samples recorded for any two equivalent values are counted in a common
        /// total count.
        /// </summary>
        /// <param name="histogram">The histogram to operate on</param>
        /// <param name="value">The given value</param>
        /// <returns>The highest value that is equivalent to the given value within the histogram's resolution.</returns>
        public static long HighestEquivalentValue(this HistogramBase histogram, long value)
        {
            return histogram.NextNonEquivalentValue(value) - 1;
        }



        /// <summary>
        /// Provide a means of iterating through histogram values according to percentile levels. 
        /// The iteration is performed in steps that start at 0% and reduce their distance to 100% according to the
        /// <paramref name="percentileTicksPerHalfDistance"/> parameter, ultimately reaching 100% when all recorded
        /// histogram values are exhausted.
        /// </summary>
        /// <param name="histogram">The histogram to operate on</param>
        /// <param name="percentileTicksPerHalfDistance">
        /// The number of iteration steps per half-distance to 100%.
        /// </param>
        /// <returns>
        /// An enumerator of <see cref="HistogramIterationValue"/> through the histogram using a
        /// <see cref="PercentileEnumerator"/>.
        /// </returns>
        public static IEnumerable<HistogramIterationValue> Percentiles(this HistogramBase histogram, int percentileTicksPerHalfDistance)
        {
            return new PercentileEnumerable(histogram, percentileTicksPerHalfDistance);
        }

        /// <summary>
        /// Produce textual representation of the value distribution of histogram data by percentile. 
        /// The distribution is output with exponentially increasing resolution, with each exponentially decreasing 
        /// half-distance containing <paramref name="percentileTicksPerHalfDistance"/> percentile reporting tick points.
        /// </summary>
        /// <param name="histogram">The histogram to operate on</param>
        /// <param name="writer">The <see cref="TextWriter"/> into which the distribution will be output</param>
        /// <param name="percentileTicksPerHalfDistance">
        /// The number of reporting points per exponentially decreasing half-distance
        /// </param>
        /// <param name="outputValueUnitScalingRatio">
        /// The scaling factor by which to divide histogram recorded values units in output.
        /// Use the <see cref="OutputScalingFactor"/> constant values to help choose an appropriate output measurement.
        /// </param>
        /// <param name="useCsvFormat">Output in CSV (Comma Separated Values) format if <c>true</c>, else use plain text form.</param>
        public static void OutputPercentileDistribution(this HistogramBase histogram,
            TextWriter writer,
            int percentileTicksPerHalfDistance = 5,
            double outputValueUnitScalingRatio = OutputScalingFactor.TicksToMilliseconds,
            bool useCsvFormat = false)
        {
            var formatter = useCsvFormat
                ? (IOutputFormatter)new CsvOutputFormatter(writer, histogram.NumberOfSignificantValueDigits, outputValueUnitScalingRatio)
                : (IOutputFormatter)new HgrmOutputFormatter(writer, histogram.NumberOfSignificantValueDigits, outputValueUnitScalingRatio);

            try
            {
                formatter.WriteHeader();
                foreach (var iterationValue in histogram.Percentiles(percentileTicksPerHalfDistance))
                {
                    formatter.WriteValue(iterationValue);
                }
                formatter.WriteFooter(histogram);
            }
            catch (ArgumentOutOfRangeException)
            {
                // Overflow conditions on histograms can lead to ArgumentOutOfRangeException on iterations:
                if (histogram.HasOverflowed())
                {
                    writer.Write("# Histogram counts indicate OVERFLOW values");
                }
                else
                {
                    // Re-throw if reason is not a known overflow:
                    throw;
                }
            }
        }

        /// <summary>
        /// Executes the action and records the time to complete the action. 
        /// The time is recorded in ticks. 
        /// Note this is a convenience method and can carry a cost.
        /// If the <paramref name="action"/> delegate is not cached, then it may incur an allocation cost for each invocation of <see cref="Record"/>
        /// </summary>
        /// <param name="histogram">The Histogram to record the latency in.</param>
        /// <param name="action">The functionality to execute and measure</param>
        /// <remarks>
        /// <para>
        /// Ticks are used as the unit of recording here as they are the smallest unit that .NET can measure
        /// and require no conversion at time of recording. Instead conversion (scaling) can be done at time
        /// of output to microseconds, milliseconds, seconds or other appropriate unit.
        /// </para>
        /// <para>
        /// If you are able to cache the <paramref name="action"/> delegate, then doing so is encouraged.
        /// <example>
        /// Here are two examples.
        /// The first does not cache the delegate
        /// 
        /// <code>
        /// for (long i = 0; i &lt; loopCount; i++)
        /// {
        ///   histogram.Record(IncrementNumber);
        /// }
        /// </code>
        /// This second example does cache the delegate
        /// <code>
        /// Action incrementNumber = IncrementNumber;
        /// for (long i = 0; i &lt; loopCount; i++)
        /// {
        ///   histogram.Record(incrementNumber);
        /// }
        /// </code>
        /// In the second example, we will not be making allocations each time i.e. an allocation of an <seealso cref="Action"/> from <code>IncrementNumber</code>.
        /// This will reduce memory pressure and therefore garbage collection penalties.
        /// For performance sensitive applications, this method may not be suitable.
        /// As always, you are encouraged to test and measure the impact for your scenario.
        /// </example>
        /// </para>
        /// </remarks>
        public static void Record(this HistogramBase histogram, Action action)
        {
            var start = Stopwatch.GetTimestamp();
            action();
            var elapsedTicks = (Stopwatch.GetTimestamp() - start);
            histogram.RecordValue(elapsedTicks);
        }
    }

}