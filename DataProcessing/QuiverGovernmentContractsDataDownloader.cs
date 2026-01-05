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
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using QuantConnect;
using QuantConnect.Configuration;
using QuantConnect.Data.Auxiliary;
using QuantConnect.Lean.Engine.DataFeeds;
using QuantConnect.Logging;
using QuantConnect.Util;

/// <summary>
/// QuiverGovernmentContractDownloader implementation.
/// </summary>
public class QuiverGovernmentContractDownloader : IDisposable
{
    public const string VendorName = "quiver";
    public const string VendorDataName = "governmentcontracts";

    private readonly int _maxRetries = 5;
    private readonly int _maximumPagesToFetch = Config.GetInt("quiver-max-pages", 100);
    private readonly string _destinationFolder;
    private readonly string _processedDataDirectory;
    private readonly string _clientKey = Config.Get("vendor-auth-token");
    private readonly Func<string, DateTime, SecurityIdentifier> _generateEquity;

    private readonly JsonSerializerSettings _jsonSerializerSettings = new()
    {
        DateTimeZoneHandling = DateTimeZoneHandling.Utc,
        NullValueHandling = NullValueHandling.Ignore
    };

    /// <summary>
    /// Control the rate of download per unit of time. Represents rate limits of 60 requests per 60 second
    /// </summary>
    private readonly RateGate _indexGate = new(Config.GetInt("rate-limit", 5), TimeSpan.FromSeconds(10));

    /// <summary>
    /// Creates a new instance of <see cref="QuiverGovernmentContracts"/>
    /// </summary>
    /// <param name="destinationFolder">The folder where the data will be saved</param>
    /// <param name="apiKey">The Vendor API key</param>
    public QuiverGovernmentContractDownloader()
    {
        var outputDirectory = Config.Get("temp-output-directory", "/temp-output-directory");
        _destinationFolder = Path.Combine(outputDirectory, "alternative", VendorName, VendorDataName);
        Directory.CreateDirectory(_destinationFolder);
        _processedDataDirectory = Path.Combine(Globals.DataFolder, "alternative", VendorName, VendorDataName);
        try 
        { 
            Directory.CreateDirectory(_processedDataDirectory); 
        }
        catch 
        {
            // We might be running in an environment where we don't have access to create directories
            // so we do nothing with the exception
        }

        var mapFileProvider = new LocalZipMapFileProvider();
        mapFileProvider.Initialize(new DefaultDataProvider());
        _generateEquity = (ticker, mappingResolveDate) => SecurityIdentifier.GenerateEquity(ticker, Market.USA, true, mapFileProvider, mappingResolveDate);
    }

    /// <summary>
    /// Runs the instance of the object with given date.
    /// </summary>
    /// <param name="processDate">The date of data to be fetched and processed</param>
    /// <returns>True if process all downloads successfully</returns>
    public bool Run(DateTime processDate)
    {
        var stopwatch = Stopwatch.StartNew();
        Log.Trace($"QuiverGovernmentContractsDataDownloader.Run(): Start downloading/processing QuiverQuant GovernmentContracts data");

        var today = DateTime.UtcNow.Date;
        try
        {
            List<RawGovernmentContract> govContractsByDate = [];

            var page = 0;
            while (page++ < _maximumPagesToFetch)
            {
                var quiverGovContractsData = HttpRequester($"live/govcontractsall?date={processDate:yyyyMMdd}&page={page}").SynchronouslyAwaitTaskResult();
                if (string.IsNullOrWhiteSpace(quiverGovContractsData) || quiverGovContractsData == "[]")
                {
                    break;
                }
                govContractsByDate.AddRange(JsonConvert.DeserializeObject<List<RawGovernmentContract>>(quiverGovContractsData, _jsonSerializerSettings));
            }

            if (govContractsByDate.IsNullOrEmpty()) return false;

            Log.Trace($@"QuiverGovernmentContractsDataDownloader.Run(): Received data on on {processDate:yyyy-MM-dd}: Last page: {page - 1}");

            Dictionary<string, List<string>> govContractsByTicker = [];

            foreach (var govContract in govContractsByDate)
            {
                var ticker = govContract.Ticker.ToUpperInvariant();

                if (!govContractsByTicker.TryGetValue(ticker, out var _))
                {
                    govContractsByTicker.Add(ticker, []);
                }

                var description = govContract.Description == null ? null : govContract.Description.Replace(",", ";").Replace("\n", " ");
                var curRow = $"{description},{govContract.Agency},{govContract.Amount}";
                var actionDate = govContract.ActionDate == DateTime.MinValue ? processDate : govContract.ActionDate;

                govContractsByTicker[ticker].Add($"{actionDate:yyyyMMdd},{curRow}");
            }

            govContractsByTicker.DoForEach(kvp => SaveContentToFile(_destinationFolder, kvp.Key, kvp.Value));
            
            if (page >= _maximumPagesToFetch)
            {
                Log.Trace($"QuiverGovernmentContractsDataDownloader.Run(): Reached maximum pages to fetch {_maximumPagesToFetch} for date {processDate:yyyy-MM-dd}. There may be more data available.");
                return false;
            }
        }
        catch (Exception e)
        {
            Log.Error(e);
            return false;
        }

        Log.Trace($"QuiverGovernmentContractsDataDownloader.Run(): Finished in {stopwatch.Elapsed.ToStringInvariant(null)}");
        return true;
    }

    public bool ProcessUniverse()
    {
        var universeFolder = Path.Combine(_destinationFolder, "universe");
        Directory.CreateDirectory(universeFolder);

        Dictionary<DateTime, HashSet<string>> dataBydate = [];

        void processFile(string filePath)
        {
            var ticker = Path.GetFileNameWithoutExtension(filePath).ToUpperInvariant();
            foreach (var line in File.ReadAllLines(filePath))
            {
                var index = line.IndexOf(',');
                if (index <= 0) continue;
                var actionDate = DateTime.ParseExact(line[..index], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
                if (!dataBydate.TryGetValue(actionDate, out var data))
                {
                    dataBydate[actionDate] = data = [];
                }
                var sid = _generateEquity(ticker, actionDate);
                if (sid.Date.Year < 1998) continue;
                data.Add($"{sid},{ticker}{line[index..]}");
            }
        }

        if (Directory.Exists(_processedDataDirectory))
        {
            Directory.EnumerateFiles(_processedDataDirectory, "*.csv").DoForEach(processFile);
        }
        Directory.EnumerateFiles(_destinationFolder, "*.csv").DoForEach(processFile);

        dataBydate.DoForEach((kvp) =>
        {
            var filePath = Path.Combine(universeFolder, $"{kvp.Key:yyyyMMdd}.csv");
            File.WriteAllLines(filePath, kvp.Value.OrderBy(x => x));
        });

        return true;
    }

    /// <summary>
    /// Sends a GET request for the provided URL
    /// </summary>
    /// <param name="url">URL to send GET request for</param>
    /// <returns>Content as string</returns>
    /// <exception cref="Exception">Failed to get data after exceeding retries</exception>
    private async Task<string> HttpRequester(string url)
    {
        var baseUrl = "https://api.quiverquant.com/beta/";

        for (var retries = 1; retries <= _maxRetries; retries++)
        {
            try
            {
                using var client = new HttpClient();
                client.BaseAddress = new Uri(baseUrl);
                client.DefaultRequestHeaders.Clear();

                // You must supply your API key in the HTTP header,
                // otherwise you will receive a 403 Forbidden response
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Token", _clientKey);

                // Responses are in JSON: you need to specify the HTTP header Accept: application/json
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                // Makes sure we don't overrun Quiver rate limits accidentally
                _indexGate.WaitToProceed();

                var response = await client.GetAsync(Uri.EscapeUriString(url));

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    Log.Error($"QuiverGovernmentContractDownloader.HttpRequester(): Files not found at url: {Uri.EscapeUriString(url)}");
                    response.DisposeSafely();
                    return string.Empty;
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    var finalRequestUri = response.RequestMessage.RequestUri; // contains the final location after following the redirect.
                    response = client.GetAsync(finalRequestUri).Result; // Reissue the request. The DefaultRequestHeaders configured on the client will be used, so we don't have to set them again.
                }

                response.EnsureSuccessStatusCode();

                var result = await response.Content.ReadAsStringAsync();
                response.DisposeSafely();

                return result;
            }
            catch (Exception e)
            {
                Log.Error(e, $"QuiverGovernmentContractDownloader.HttpRequester(): Error at HttpRequester. (retry {retries}/{_maxRetries})");
                Thread.Sleep(1000);
            }
        }

        throw new Exception($"Request for {baseUrl}{url} failed with no more retries remaining (retry {_maxRetries}/{_maxRetries})");
    }

    /// <summary>
    /// Saves contents to disk, deleting existing zip files
    /// </summary>
    /// <param name="destinationFolder">Final destination of the data</param>
    /// <param name="name">file name</param>
    /// <param name="contents">Contents to write</param>
    private void SaveContentToFile(string destinationFolder, string name, IEnumerable<string> contents)
    {
        name = name.ToLowerInvariant();
        var finalPath = Path.Combine(destinationFolder, $"{name}.csv");
        var filePath = Path.Combine(_processedDataDirectory, $"{name}.csv");
        
        var finalFileExists = File.Exists(filePath);
        if (!finalFileExists)
        {
            filePath = finalPath;
            finalFileExists = File.Exists(filePath);
        }

        var lines = new HashSet<string>(contents);
        if (finalFileExists)
        {
            foreach (var line in File.ReadAllLines(filePath))
            {
                lines.Add(line);
            }
        }

        File.WriteAllLines(finalPath, lines.OrderBy(x => Parse.DateTimeExact(x[..8], "yyyyMMdd")));
    }

    private class RawGovernmentContract
    {
        /// <summary>
        /// Date that the GovernmentContracts spend was reported
        /// </summary>
        [JsonProperty(PropertyName = "Date")]
        [JsonConverter(typeof(DateTimeJsonConverter), "yyyy-MM-dd")]
        public DateTime Date { get; set; }

        /// <summary>
        /// Date that the GovernmentContracts spend was reported
        /// </summary>
        [JsonProperty(PropertyName = "action_date")]
        [JsonConverter(typeof(DateTimeJsonConverter), "yyyy-MM-dd")]
        public DateTime ActionDate { get; set; }

        /// <summary>
        /// The ticker/symbol for the company
        /// </summary>
        [JsonProperty(PropertyName = "Ticker")]
        public string Ticker { get; set; }

        /// <summary>
        /// Contract description
        /// </summary>
        [JsonProperty(PropertyName = "Description")]
        public string Description { get; set; }

        /// <summary>
        /// Awarding Agency Name
        /// </summary>
        [JsonProperty(PropertyName = "Agency")]
        public string Agency { get; set; }

        /// <summary>
        /// Total dollars obligated under the given contract
        /// </summary>
        [JsonProperty(PropertyName = "Amount")]
        public decimal Amount { get; set; }
    }

    /// <summary>
    /// Disposes of unmanaged resources
    /// </summary>
    public void Dispose()
    {
        _indexGate?.Dispose();
    }
}