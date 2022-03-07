using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Fluid;
using Microsoft.AspNetCore.Http;
using static VivistaServer.CommonController;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace VivistaServer
{
    
    public class Request
    {
        public DateTime timestamp;
        public double ms;
        public RequestInfo requestInfo;
        public string endpoint;
    }

    public class RequestData
    {
        public string endpoint;
        public double median;
        public double average;
        public DateTime timestamp;
        public double percentile95;
        public double percentile99;
        public int countrequests;
        public int downloads;
    }

    public class EndpointPercentile
    {
        public double percentile95;
        public string endpoint;
    }

    public class RequestInfo
    {
        public PathString path { get; set; }
        public string method { get; set; }
        public IQueryCollection query { get; set; }

        public IFormCollection form { get; set; }
    }

    public class DashboardController
    {
        public static int downloads = 0;
        private static List<Request> cachedRequests = new List<Request>();

        [Route("GET", "/admin/dashboard")]
        private static async Task DashboardGet(HttpContext context)
        {
            SetHTMLContentType(context);
            //TODO: Multiply database connections  
            var userTask = Task.Run(async () =>
            {
                var connection = Database.OpenNewConnection();
                return await connection.QueryAsync<int>("select COUNT(*) FROM users;");
            });

            var videoTask = Task.Run(async () =>
            {
                var connection = Database.OpenNewConnection();
                return await connection.QueryAsync<int>("SELECT COUNT(*) FROM videos;");
            });

            var downloadTask = Task.Run(async () =>
            {
                var connection = Database.OpenNewConnection();
                return await connection.QueryAsync<int>("SELECT SUM(downloads) FROM videos;");
            });

            Task.WaitAll(userTask, videoTask, downloadTask);

            var templateContext = new TemplateContext(new
            {
                users = userTask.Result,
                videos = videoTask.Result,
                downloads = downloadTask.Result
            });

            await context.Response.WriteAsync(await HTMLRenderer.Render(context, "Templates\\dashboard.liquid", templateContext));
        }

        public static void AddDownloads()
        {
            downloads++;
        }

        public static void AddRequestToCache(Request request)
        {
            request.timestamp = DateTime.UtcNow;

            cachedRequests.Add(request);
        }

        public static void AddMinuteData()
        {
            //TODO: Dictionary
            var a = new Dictionary<string, Request>().GroupBy(x => x.Key);
            if (cachedRequests.Count > 0)
            {
                var groupedRequests = cachedRequests.GroupBy(s => s.endpoint).Select(grp => grp.ToList()).ToList();

                foreach (var specificEndpointList in groupedRequests)
                {
                    //Percentile
                    double[] ms = specificEndpointList.Select(c => c.ms).ToArray();
                    var percentile95 = Percentile(ms, 0.95);
                    var percentile99 = Percentile(ms, 0.99);

                    //Median
                    specificEndpointList.Sort((r1, r2) => r1.ms.CompareTo(r2.ms));
                    double median = 0;
                    if (specificEndpointList.Count % 2 == 1)
                    {
                        median = specificEndpointList[specificEndpointList.Count / 2].ms;
                    }
                    else
                    {
                        var firstValue = specificEndpointList[specificEndpointList.Count / 2 - 1].ms;
                        var secondValue = specificEndpointList[specificEndpointList.Count / 2].ms;
                        median = (firstValue + secondValue) / 2;
                    }

                    //Average
                    double average = 0;
                    specificEndpointList.ForEach((req) => average += req.ms);
                    average = average / specificEndpointList.Count;

                    //timestamp
                    var timestamp = specificEndpointList[specificEndpointList.Count / 2].timestamp;
                    //round down to xx:xx:00
                    timestamp = timestamp.RoundUp(TimeSpan.FromSeconds(60)).AddMinutes(-1);

                    using var connection = Database.OpenNewConnection();
                    connection.Execute(
                        @"insert into statistics_minutes(median, average, timestamp, countrequests, percentile95, percentile99, endpoint, downloads) 
                                values(@median, @average, @timestamp, @countrequests, @percentile95, @percentile99, @endpoint, @downloads);",
                        new
                        {
                            median,
                            average,
                            timestamp,
                            countrequests = specificEndpointList.Count,
                            percentile95,
                            percentile99,
                            endpoint = specificEndpointList[0].endpoint,
                            downloads
                        });
                    downloads = 0;

                    //Outliers
                    var percentilesPerEndpoint = connection.Query<EndpointPercentile>(
                        @"SELECT avg(percentile95) as percentile95, endpoint
                        FROM statistics_days
                        GROUP BY endpoint;").ToDictionary(p => p.endpoint, p => p.percentile95);

                    foreach (var req in cachedRequests)
                    {
                        var outlierLimit = percentilesPerEndpoint[req.endpoint] > 0 ? percentilesPerEndpoint[req.endpoint] * 2 : double.MaxValue;
                        if (req.ms > outlierLimit)
                        {
                            var reqinfo = SerializeToBson(req.requestInfo);
                            connection.Execute(@"insert into statistics_outliers(timestamp, ms, reqinfo) values(@timestamp, @ms, @reqinfo::jsonb);",
                                new { timestamp = req.timestamp, ms = req.ms, reqinfo = reqinfo });
                        }
                    }
                    cachedRequests.Clear();
                }



            }
        }

        public static void AddHourData(DateTime startTime)
        {
            var connection = Database.OpenNewConnection();

            //round down to xx:00:00
            startTime = startTime.RoundUp(TimeSpan.FromMinutes(60)).AddHours(-1);

            var endTime = startTime.AddHours(1);

            var minutesData = (List<RequestData>)connection.Query<RequestData>(
                @"SELECT median, average, timestamp, countrequests, percentile95, percentile99, endpoint, downloads
                    FROM statistics_minutes  
                    WHERE timestamp >= @startTime AND timestamp < @endTime;",
                new { startTime, endTime });
            if (minutesData.Count > 0)
            {
                var groupedData = minutesData.GroupBy(s => s.endpoint).Select(grp => grp.ToList()).ToList();
                foreach (var specificEndpointList in groupedData)
                {

                    var hourData = GetMedianAverageTimestamp(specificEndpointList);
                    var timestamp = hourData.timestamp;

                    timestamp = startTime.RoundUp(TimeSpan.FromMinutes(60)).AddHours(-1);

                    hourData.timestamp = timestamp;

                    connection.Execute(
                        @"insert into statistics_hours(median, average, timestamp, countrequests, percentile95, percentile99, endpoint) 
                            values(@median, @average, @timestamp, @countrequests, @percentile95, @percentile99, @endpoint);",
                        new
                        {
                            median = hourData.median,
                            average = hourData.average,
                            timestamp = hourData.timestamp,
                            countrequests = hourData.countrequests,
                            percentile95 = hourData.percentile95,
                            percentile99 = hourData.percentile99,
                            endpoint = specificEndpointList[0].endpoint,
                            downloads = hourData.downloads
                        });

                }
            }
        }

        public static void AddDayData(DateTime startTime)
        {
            var connection = Database.OpenNewConnection();

            //round down to 00:00:00
            startTime = startTime.RoundUp(TimeSpan.FromHours(24)).AddDays(-1);

            var endTime = startTime.AddDays(1);

            var hoursData = (List<RequestData>)connection.Query<RequestData>(
                @"SELECT median, average, timestamp, countrequests, percentile95, percentile99, endpoint, downloads
                    FROM statistics_hours  
                    WHERE timestamp >= @startTime AND timestamp < @endTime;",
                new { startTime, endTime });
            if (hoursData.Count > 0)
            {
                var groupedData = hoursData.GroupBy(s => s.endpoint).Select(grp => grp.ToList()).ToList();
                foreach (var specificEndpointList in groupedData)
                {
                    var dayData = GetMedianAverageTimestamp(specificEndpointList);
                    var timestamp = dayData.timestamp;

                    timestamp = timestamp.RoundUp(TimeSpan.FromHours(24)).AddDays(-1);

                    dayData.timestamp = timestamp;


                    connection.Execute(
                        @"insert into statistics_days(median, average, timestamp, countrequests, percentile95, percentile99, endpoint, downloads) 
                        values(@median, @average, @timestamp, @countrequests, @percentile95, @percentile99, @endpoint, @downloads);",
                        new
                        {
                            median = dayData.median,
                            average = dayData.average,
                            timestamp = dayData.timestamp,
                            countrequests = dayData.countrequests,
                            percentile95 = dayData.percentile95,
                            percentile99 = dayData.percentile99,
                            endpoint = specificEndpointList[0].endpoint,
                            downloads = dayData.downloads
                        });
                }

            }
        }


        private static RequestData GetMedianAverageTimestamp(List<RequestData> requestData)
        {
            //Percentile
            double[] ms95 = requestData.Select(c => c.percentile95).ToArray();
            double[] ms99 = requestData.Select(c => c.percentile95).ToArray();
            var percentile95 = Percentile(ms95, 0.95);
            var percentile99 = Percentile(ms99, 0.99);


            requestData.Sort((r1, r2) => r1.median.CompareTo(r2.median));
            double median = 0;
            if (requestData.Count % 2 == 1)
            {
                median = requestData[requestData.Count / 2].median;
            }
            else
            {
                var firstValue = requestData[requestData.Count / 2 - 1].median;
                var secondValue = requestData[requestData.Count / 2].median;
                median = (firstValue + secondValue) / 2;
            }

            //Average
            double average = 0;
            requestData.ForEach(req => average += req.average);
            average = average / requestData.Count;

            //timestamp
            var timestamp = requestData[requestData.Count / 2].timestamp;

            //countrequests
            var countrequests = 0;
            requestData.ForEach(req => countrequests += req.countrequests);

            //downloads
            var countDownloads = 0;
            requestData.ForEach(req => countDownloads += req.countrequests);

            return new RequestData
            {
                median = median,
                average = average,
                timestamp = timestamp,
                countrequests = countrequests,
                percentile95 = percentile95,
                percentile99 = percentile99,
                downloads = countDownloads
            };
        }


        private static double Percentile(double[] sequence, double excelPercentile)
        {
            Array.Sort(sequence);
            int N = sequence.Length;
            double n = (N - 1) * excelPercentile + 1;
            // Another method: double n = (N + 1) * excelPercentile;
            if (n == 1d) return sequence[0];
            else if (n == N) return sequence[N - 1];
            else
            {
                int k = (int)n;
                double d = n - k;
                return sequence[k - 1] + d * (sequence[k] - sequence[k - 1]);
            }
        }

        private static string SerializeToBson(RequestInfo requestInfo)
        {
            string jsonString = JsonSerializer.Serialize(requestInfo);
            return jsonString;
        }
    }

}
