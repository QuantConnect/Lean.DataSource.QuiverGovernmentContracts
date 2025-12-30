/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
*/

using System;
using QuantConnect;
using QuantConnect.Logging;
using QuantConnect.Util;

/// <summary>
/// Entrypoint for the data downloader/converter
/// </summary>
public class Program
{
    /// <summary>
    /// Entrypoint of the program
    /// </summary>
    /// <returns>Exit code. 0 equals successful, and any other value indicates the downloader/converter failed.</returns>
    public static void Main()
    {
        // Get the config values first before running. These values are set for us
        // automatically to the value set on the website when defining this data type
        var processingDateValue = Environment.GetEnvironmentVariable("QC_DATAFLEET_DEPLOYMENT_DATE");
        var processingDate = string.IsNullOrWhiteSpace(processingDateValue)
            ? DateTime.UtcNow.AddDays(-1)
            : Parse.DateTimeExact(processingDateValue, "yyyyMMdd");
        
        // Dataset starts from 2022-04-22
        var datasetStartDate = new DateTime(2022, 4, 21);
        if (processingDate < datasetStartDate)
        {
            Log.Error($"QuantConnect.DataProcessing.Program.Main(): Invalid processing date, must be greater than {datasetStartDate:yyyyMMdd}.");
            Environment.Exit(1);
        }

        QuiverGovernmentContractDownloader instance = null;
        try
        {
            // Pass in the values we got from the configuration into the downloader/converter.
            instance = new QuiverGovernmentContractDownloader();
        }
        catch (Exception err)
        {
            Log.Error(err, $"QuantConnect.DataProcessing.Program.Main(): The downloader/converter for {QuiverGovernmentContractDownloader.VendorDataName} {QuiverGovernmentContractDownloader.VendorDataName} data failed to be constructed");
            Environment.Exit(1);
        }
 
        // No need to edit anything below here for most use cases.
        // The downloader/converter is ran and cleaned up for you safely here.
        try
        {
            // Run the data downloader/converter.
            if (!instance.Run(processingDate))
            {
                Log.Error($"QuantConnect.DataProcessing.Program.Main(): Failed to download/process {QuiverGovernmentContractDownloader.VendorName} {QuiverGovernmentContractDownloader.VendorDataName} data for date: {processingDate:yyyy-MM-dd}");
            }
            
            // Process the universe data after all dates have been processed
            instance.ProcessUniverse();
        }
        catch (Exception err)
        {
            Log.Error(err, $"QuantConnect.DataProcessing.Program.Main(): The downloader/converter for {QuiverGovernmentContractDownloader.VendorDataName} {QuiverGovernmentContractDownloader.VendorDataName} data exited unexpectedly");
            Environment.Exit(1);
        }
        finally
        {
            // Run cleanup of the downloader/converter once it has finished or crashed.
            instance.DisposeSafely();
        }

        // The downloader/converter was successful
        Environment.Exit(0);
    }
}