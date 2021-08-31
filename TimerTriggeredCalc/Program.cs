﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using OSIsoft.AF;
using OSIsoft.AF.Asset;
using OSIsoft.AF.Data;
using OSIsoft.AF.Time;
using Timer = System.Timers.Timer;

namespace TimerTriggeredCalc
{
    public static class Program
    {
        private static readonly List<AFElement> _contextList = new List<AFElement>();
        private static AFDataCache _myAFDataCache;
        private static AFKeyedResults<AFAttribute, AFData> _dataCaches;
        private static Exception _toThrow;
        private static Timer _aTimer;

        /// <summary>
        /// Entry point of the program
        /// </summary>
        public static void Main()
        {
            // Create a cancellation token source in order to cancel the calculation loop on demand
            var source = new CancellationTokenSource();
            var token = source.Token;

            // Launch the sample's main loop, passing it the cancellation token
            var success = MainLoop(token);

            // Pause until the user decides to end the loop
            Console.WriteLine($"Press <ENTER> to end... ");
            Console.ReadLine();

            // Cancel the operation and wait until everything is canceled properly
            source.Cancel();
            _ = success.Result;

            // Dispose of the cancellation token source and exit the program
            if (source != null)
            {
                Console.WriteLine("Disposing cancellation token source...");
                source.Dispose();
            }

            Console.WriteLine("Quitting Main...");
        }

        /// <summary>
        /// This function loops until manually stopped, triggering the calculation event on the prescribed timer.
        /// If being tested, it stops after the set amount of time
        /// </summary>
        /// <param name="token">Controls if the loop should stop and exit</param>
        /// <returns>true if successful</returns>
        public static async Task<bool> MainLoop(CancellationToken token)
        {
            try
            {
                #region configurationSettings
                AppSettings settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Directory.GetCurrentDirectory() + "/appsettings.json"));
                #endregion // configurationSettings

                #region step1
                Console.WriteLine("Resolving AF Server object...");

                var myPISystems = new PISystems();
                PISystem myPISystem;

                if (string.IsNullOrWhiteSpace(settings.AFServerName))
                {
                    // Use the default PI Data Archive
                    myPISystem = myPISystems.DefaultPISystem;
                }
                else
                {
                    myPISystem = myPISystems[settings.AFServerName];
                }

                Console.WriteLine("Resolving AF Database object...");

                AFDatabase myAFDB;

                if (string.IsNullOrWhiteSpace(settings.AFDatabaseName))
                {
                    // Use the default PI Data Archive
                    myAFDB = myPISystem.Databases.DefaultDatabase;
                }
                else
                {
                    myAFDB = myPISystem.Databases[settings.AFDatabaseName];
                }
                #endregion // step1

                #region step2
                Console.WriteLine("Resolving AFAttributes to add to the Data Cache...");

                var attributeCacheList = DetermineListOfIdealGasLawCalculationAttributes(myAFDB, settings.Contexts);
                #endregion // step2

                #region step3
                Console.WriteLine("Creating a data cache for snapshot event updates...");

                _myAFDataCache = new AFDataCache();
                _dataCaches = _myAFDataCache.Add(attributeCacheList);
                _myAFDataCache.CacheTimeSpan = new TimeSpan(settings.CacheTimeSpanSeconds * TimeSpan.TicksPerSecond);

                // Create a timer with the specified interval of checking for updates
                _aTimer = new Timer()
                {
                    Interval = settings.TimerIntervalMS,
                    AutoReset = true,
                };

                // Add the calculation to the timer's elapsed trigger event handler list
                _aTimer.Elapsed += TriggerCalculation;
                #endregion // step3

                #region step4
                if (settings.DefineOffsetSeconds)
                {
                    Console.WriteLine($"Pausing until the defined offset of {settings.OffsetSeconds} seconds...");
                    var now = DateTime.Now;
                    var secondsUntilOffset = (60 + (settings.OffsetSeconds - now.Second)) % 60;
                    Thread.Sleep((secondsUntilOffset * 1000) - now.Millisecond);
                }
                else
                {
                    Console.WriteLine("Not pausing until a define offset");
                }

                // Enable the timer and have it reset on each trigger
                _aTimer.Enabled = true;
                #endregion // step4

                #region step5
                Console.WriteLine("Triggering the initial calculation...");
                
                PerformAllCalculations(DateTime.Now);
                #endregion // step5

                #region step6
                Console.WriteLine("Waiting for cancellation token to be triggered...");

                await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
                #endregion //step6
            }
            catch (TaskCanceledException)
            {
                // Task cancellation is done via exception but shouldn't denote a failure
                Console.WriteLine("Task canceled successfully");
            }
            catch (Exception ex)
            {
                // All other exceptions should be treated as a failure
                Console.WriteLine(ex);
                _toThrow = ex;
                throw;
            }
            finally
            {
                try
                {
                    if (_aTimer != null)
                    {
                        Console.WriteLine("Disposing timer object...");
                        _aTimer.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to dispose timer object. Error: {ex.Message}");
                }

                try
                {
                    if (_myAFDataCache != null)
                    {
                        Console.WriteLine("Disposing AF Data Cache...");
                        _myAFDataCache.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to dispose data cache object. Error: {ex.Message}");
                }
            }
                
            Console.WriteLine("Quitting...");
            return _toThrow == null;
        }

        /// <summary>
        /// This method determines the AFAttribute objects to add to the data cache. 
        /// The attributes are hard coded for this calculation, so the logic is abstracted out of the main method
        /// </summary>
        /// <param name="myAFDB">The AF Database the calculation is running against</param>
        /// <param name="elementContexts">The list of element names from the appsettings /param>
        /// <returns>A list of AFAttribute objects to be added to the data cache</returns>
        private static List<AFAttribute> DetermineListOfIdealGasLawCalculationAttributes(AFDatabase myAFDB, IList<string> elementContexts)
        {
            var attributeCacheList = new List<AFAttribute>();

            // Resolve the input and output tag names to PIPoint objects
            foreach (var context in elementContexts)
            {
                try
                {
                    // Resolve the element from its name
                    var thisElement = myAFDB.Elements[context];

                    // Make a list of inputs to ensure a partially failed context resolution doesn't add to the data cache
                    var thisattributeCacheList = new List<AFAttribute>
                    {
                        // Resolve each input attribute
                        thisElement.Attributes["Temperature"],
                        thisElement.Attributes["Pressure"],
                        thisElement.Attributes["Volume"],
                        thisElement.Attributes["Moles"],
                    };

                    // If successful, add the list of resolved attributes to the data cache list and the element to the context list
                    _contextList.Add(thisElement);
                    attributeCacheList.AddRange(thisattributeCacheList);
                }
                catch (Exception ex)
                {
                    // If not successful, inform the user and move on to the next pair
                    Console.WriteLine($"Context {context} will be skipped due to error: {ex.Message}");
                }
            }

            return attributeCacheList;
        }

        /// <summary>
        /// This function triggers the calculation to be run against the timestamp of the timer event
        /// </summary>
        /// <param name="source">The source of the event</param>
        /// <param name="e">An ElapsedEventArgs object that contains the event data</param>
        private static void TriggerCalculation(object source, ElapsedEventArgs e)
        {
            _myAFDataCache.UpdateData();
            PerformAllCalculations(e.SignalTime);
        }

        /// <summary>
        /// Wrapper function that abstracts the iteration of contexts from the calculation logic itself
        /// </summary>
        /// <param name="triggerTime">The timestamp to perform the calculation against</param>
        private static void PerformAllCalculations(DateTime triggerTime)
        {
            foreach (var context in _contextList)
            {
                PerformCalculation(triggerTime, context);
            }
        }

        /// <summary>
        /// This function performs the calculation and writes the value to the output tag
        /// <param name="triggerTime">The timestamp to perform the calculation against</param>
        /// <param name="context">The context on which to perform this calculation</param>
        private static void PerformCalculation(DateTime triggerTime, AFElement context)
        {
            // Configuration
            var numValues = 100;  // number of values to find the trimmed average of
            var isForward = false;
            var tempUom = "K";
            var pressUom = "torr";
            var volUom = "L";
            var molUom = "mol";
            var molRateUom = "mol/s";
            var filterExpression = string.Empty;
            var includeFilteredValues = false;

            var numStDevs = 1.75; // number of standard deviations of variance to allow

            // Obtain the recent values from the trigger timestamp
            var afTempVals = GetData(context.Attributes["Temperature"]).RecordedValuesByCount(triggerTime, numValues, isForward, AFBoundaryType.Interpolated, context.PISystem.UOMDatabase.UOMs[tempUom], filterExpression, includeFilteredValues);
            var afPressVals = GetData(context.Attributes["Pressure"]).RecordedValuesByCount(triggerTime, numValues, isForward, AFBoundaryType.Interpolated, context.PISystem.UOMDatabase.UOMs[pressUom], filterExpression, includeFilteredValues);
            var afVolumeVal = GetData(context.Attributes["Volume"]).EndOfStream(context.PISystem.UOMDatabase.UOMs[volUom]);

            // Remove bad values
            afTempVals.RemoveAll(afval => !afval.IsGood);
            afPressVals.RemoveAll(afval => !afval.IsGood);

            // Iteratively solve for the trimmed mean of temperature and pressure
            var meanTemp = GetTrimmedMean(afTempVals, numStDevs);
            var meanPressure = GetTrimmedMean(afPressVals, numStDevs);

            // Apply the Ideal Gas Law (PV = nRT) to solve for number of moles
            var gasConstant = 62.363598221529; // units of  L * Torr / (K * mol)
            var currentMolarValue = meanPressure * afVolumeVal.ValueAsDouble() / (gasConstant * meanTemp); // PV = nRT; n = PV/(RT)

            // Before writing to the output attribute, find the previous value to determine rate of change
            AFValue previousMolarValue = null;
            try
            {
                previousMolarValue = GetData(context.Attributes["Moles"]).RecordedValuesByCount(triggerTime, 1, isForward, AFBoundaryType.Inside, context.PISystem.UOMDatabase.UOMs[molUom], filterExpression, includeFilteredValues)[0];
            }
            catch
            {
                Console.WriteLine($"Previous value not found for {triggerTime}");
            }

            // Write to output attribute's data cache since we want to use this value in the next section
            // Using the cache ensures the value is robustly read back and does not have to travel through the Data Archive and back
            GetData(context.Attributes["Moles"]).UpdateValue(new AFValue(currentMolarValue, triggerTime, context.PISystem.UOMDatabase.UOMs[molUom]), AFUpdateOption.Insert);

            // If there are not yet two values, or one of them was bad, do not calculate the rate
            if (previousMolarValue == null || !previousMolarValue.IsGood)
            {
                Console.WriteLine($"Insufficient value count to determine molar rate of change at {triggerTime}. Skipping this calculation...");
                return;
            }

            // Find the rate and write it to the attribute
            // This attribute is not read by this calculation, so writing to the cache is not necessary
            var molarRateOfChange = (currentMolarValue - previousMolarValue.ValueAsDouble()) / (new AFTimeSpan(new AFTime(triggerTime) - previousMolarValue.Timestamp).Ticks / TimeSpan.TicksPerSecond);
            context.Attributes["MolarFlowRate"].Data.UpdateValue(new AFValue(molarRateOfChange, triggerTime, context.PISystem.UOMDatabase.UOMs[molRateUom]), AFUpdateOption.Insert);
        }

        /// <summary>
        /// This method returns the AFData object from the cache if it exists, otherwise returns the attribute's non-cached AFData object
        /// </summary>
        /// <param name="attribute">The AFAttribute whose AFData object is being requested</param>
        /// <returns>The cached, if possible, otherwise non-cached AFData object for the requested attribute</returns>
        private static AFData GetData(AFAttribute attribute)
        {
            if (_myAFDataCache.TryGetItem(attribute, out var data))
                return data;
            else
                return attribute.Data;
        }

        /// <summary>
        /// This method finds the mean of a set of AFValues after removing the outliers in an iterative fashion
        /// </summary>
        /// <param name="afvals">List of values to be summarized</param>
        /// <param name="numberOfStandardDeviations">The cutoff for outliers</param>
        /// <returns>The mean of the non-outlier values</returns>
        private static double GetTrimmedMean(AFValues afvals, double numberOfStandardDeviations)
        {
            while (true)
            {
                // Don't loop if all values have been removed
                if (afvals.Count > 0)
                {
                    // Calculate the mean
                    var total = 0.0;
                    foreach (var afval in afvals)
                    {
                        total += afval.ValueAsDouble();
                    }

                    var mean = total / afvals.Count;

                    // Calculate the standard deviation
                    var totalSquareVariance = 0.0;
                    foreach (var afval in afvals)
                    {
                        totalSquareVariance += Math.Pow(afval.ValueAsDouble() - mean, 2);
                    }

                    var meanSqDev = totalSquareVariance / (double)afvals.Count;
                    var stdev = Math.Sqrt(meanSqDev);

                    // Determine the values outside of the boundaries, and remove them
                    var cutoff = stdev * numberOfStandardDeviations;
                    var startingCount = afvals.Count;

                    afvals.RemoveAll(afval => Math.Abs(afval.ValueAsDouble() - mean) > cutoff);

                    // If no items were removed, output the average and break the loop
                    if (afvals.Count == startingCount)
                    {
                        return mean;
                    }
                }
                else
                {
                    throw new Exception("All values were eliminated. No mean could be calculated");
                }
            }
        }
    }
}
