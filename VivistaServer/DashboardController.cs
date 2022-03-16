using Dapper;
using Fluid;
using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static VivistaServer.CommonController;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace VivistaServer
{

    public class Request
    {
        public DateTime timestamp;
        public float seconds;
        public RequestInfo requestInfo;
        public string endpoint;
        public float renderTime;
        public float dbExecTime;
    }

    public class RequestData
    {
        public string endpoint;
        public float median;
        public float average;
        public DateTime timestamp;
        public float percentile95;
        public float percentile99;
        public long countrequests;
        public float renderTime;
        public float dbExecTime;
    }

    public class GeneralData
    {
        public DateTime timestamp;
        public int downloads;
        public long views;
        public long privateMemory;
        public long workingSet;
        public long virtualMemory;
        public int uploads;
        public int uncaughtExceptions;
    }

    public class EndpointPercentile
    {
        public float percentile95;
        public string endpoint;
    }

    //NOTE(Tom): Needs to be in a separate class for serialization to bson
    public class RequestInfo
    {
        public PathString path { get; set; }
        public string method { get; set; }
        public IQueryCollection query { get; set; }
        public IFormCollection form { get; set; }
    }

    public class DashboardController
    {
        public const string DB_EXEC_TIME = "dbExecTime";
        public const string RENDER_TIME = "renderTime";

        public static int downloads = 0;
        private static int views = 0;
        private static int uploads = 0;
        private static int uncaughtExceptions = 0;
        private static List<Request> cachedRequests = new List<Request>();
        private static readonly Object cachedRequestLock = new Object();

        [Route("GET", "/admin/dashboard")]
        private static async Task DashboardGet(HttpContext context)
        {
            SetHTMLContentType(context);
            //TODO: Multiply database connections  
            var userTask = Task.Run(async () =>
            {
	            await using var connection = Database.OpenNewConnection();
                return await Database.QueryAsync<int>(connection, "SELECT COUNT(*) FROM users;", context);
            });

            var videoTask = Task.Run( async () =>
            {
	            await using var connection = Database.OpenNewConnection();
                return await Database.QueryAsync<int>(connection, "SELECT COUNT(*) FROM videos;", context);
            });

            var downloadTask = Task.Run(async () =>
            {
	            await using var connection = Database.OpenNewConnection();
                return await Database.QueryAsync<int>(connection, "SELECT SUM(downloads) FROM videos;", context);
            });

            var minuteData = Task.Run(async () =>
            {
	            await using var connection = Database.OpenNewConnection();
                return await Database.QueryAsync<RequestData>(connection, "SELECT * FROM statistics_minutes;", context);
            });

            Task.WaitAll(userTask, videoTask, downloadTask, minuteData);

            var templateContext = new TemplateContext(new
            {
                users = userTask.Result,
                videos = videoTask.Result,
                downloads = downloadTask.Result,
                averagesMinutes = minuteData.Result.Select(s => new {x = s.timestamp, y = s.average, endpoint = s.endpoint}).ToList(),
                endpoints = minuteData.Result.Select(s => s.endpoint).Distinct().ToList(),
                timeUnit = "minute"
            });

            await context.Response.WriteAsync(await HTMLRenderer.Render(context, "Templates\\dashboard.liquid", templateContext));
        }


        public static void AddDownloads()
        {
	        Interlocked.Increment(ref downloads);
        }

        public static void AddViews()
        {
	        Interlocked.Increment(ref views);
        }

        public static void AddUpload()
        {
	        Interlocked.Increment(ref uploads);
        }

        public static void AddUnCaughtException()
        {
	        Interlocked.Increment(ref uncaughtExceptions);
        }

        public static void AddRequestToCache(Request request)
        {
	        request.timestamp = DateTime.UtcNow;
            lock (cachedRequestLock)
            {
	            cachedRequests.Add(request);
            }
            
        }


        public static void AddMinuteData()
        {
            using var connection = Database.OpenNewConnection();
            if (cachedRequests.Count > 0)
            {
                var groupedRequests = cachedRequests.GroupBy(s => s.endpoint).Select(grp => grp.ToList()).ToList();
                var timestamp = new DateTime();

                //timestamp
                timestamp = groupedRequests[0][0].timestamp;
                //round down to xx:xx:00
                timestamp = timestamp.RoundUp(TimeSpan.FromSeconds(60)).AddMinutes(-1);

                foreach (var specificEndpointList in groupedRequests)
                {
                    //Percentile
                    float[] ms = specificEndpointList.Select(c => c.seconds).ToArray();
                    //Note(Tom): Array needs to be sorted
                    specificEndpointList.Sort((r1, r2) => r1.seconds.CompareTo(r2.seconds));
                    var percentile95 = Percentile(ms, 0.95f);
                    var percentile99 = Percentile(ms, 0.99f);

                    //Median
                    float median = 0;
                    if (specificEndpointList.Count % 2 == 1)
                    {
                        median = specificEndpointList[specificEndpointList.Count / 2].seconds;
                    }
                    else
                    {
                        var firstValue = specificEndpointList[specificEndpointList.Count / 2 - 1].seconds;
                        var secondValue = specificEndpointList[specificEndpointList.Count / 2].seconds;
                        median = (firstValue + secondValue) / 2;
                    }

                    //Average, average render time and average db exec time
                    float average = 0;
                    float averageRenderTime = 0;
                    float averageDbExecTime = 0;
                    foreach (var req in specificEndpointList)
                    {
                        average += req.seconds;
                        averageRenderTime += req.renderTime;
                        averageDbExecTime += req.dbExecTime;
                    }

                    average = average / specificEndpointList.Count;
                    averageRenderTime = average / specificEndpointList.Count;
                    averageDbExecTime = averageDbExecTime / specificEndpointList.Count;

                    connection.Execute(
                        @"INSERT INTO statistics_minutes(median, average, timestamp, countrequests, percentile95, percentile99, endpoint, render_time, db_exec_time) 
                                VALUES(@median, @average, @timestamp, @countrequests, @percentile95, @percentile99, @endpoint, @renderTime, @dbExecTime);",
                        new
                        {
                            median,
                            average,
                            timestamp,
                            countrequests = specificEndpointList.Count,
                            percentile95,
                            percentile99,
                            endpoint = specificEndpointList[0].endpoint,
                            renderTime = averageRenderTime,
                            dbExecTime = averageDbExecTime
                        });


                    //Outliers
                    var percentilesPerEndpoint = connection.Query<EndpointPercentile>(
                        @"SELECT avg(percentile95) as percentile95, endpoint
                        FROM statistics_days
                        GROUP BY endpoint;").ToDictionary(p => p.endpoint, p => p.percentile95);

                    foreach (var req in cachedRequests)
                    {
	                    float outlierThreshold = 0;
                        outlierThreshold = percentilesPerEndpoint.ContainsKey(req.endpoint)
                            ? percentilesPerEndpoint[req.endpoint] * 2
                            : float.MaxValue;

                        if (req.seconds > outlierThreshold)
                        {
                            var reqinfo = SerializeToBson(req.requestInfo);
                            connection.Execute(@"insert into statistics_outliers(timestamp, seconds, reqinfo) values(@timestamp, @seconds, @reqinfo::jsonb);",
                                new { timestamp = req.timestamp, ms = req.seconds, reqinfo = reqinfo });
                        }
                    }
                    cachedRequests.Clear();
                }

                Process.GetCurrentProcess().Refresh();
                var privateMemory = Process.GetCurrentProcess().PrivateMemorySize64;
                var workingSet = Process.GetCurrentProcess().WorkingSet64;
                var virtualMemory = Process.GetCurrentProcess().VirtualMemorySize64;
                //GeneralData
                connection.Execute(
                    @"INSERT INTO statistics_general_minutes(timestamp, downloads, views, private_memory, working_set, virtual_memory, uploads, uncaught_exceptions)
                            VALUES(@timestamp, @downloads, @views, @privateMemory, @workingSet, @virtualMemory, @uploads, @uncaughtExceptions);",
                    new
                    {
                        timestamp,
                        downloads,
                        views,
                        privateMemory,
                        workingSet,
                        virtualMemory,
                        uploads,
                        uncaughtExceptions
                    });
                downloads = 0;
                views = 0;
                uploads = 0;
                uncaughtExceptions = 0;
            }
        }

        public static void AddHourData(DateTime startTime)
        {
            using var connection = Database.OpenNewConnection();

            //round down to xx:00:00
            startTime = startTime.RoundUp(TimeSpan.FromMinutes(60)).AddHours(-1);

            var endTime = startTime.AddHours(1);

            var minutesData = (List<RequestData>)connection.Query<RequestData>(
                @"SELECT median, average, timestamp, countrequests, percentile95, percentile99, endpoint, render_time, db_exec_time as dbExecTime
                    FROM statistics_minutes  
                    WHERE timestamp >= @startTime AND timestamp < @endTime;",
                new { startTime, endTime });

            var minutesGeneralData = (List<GeneralData>)connection.Query<GeneralData>(
                @"SELECT timestamp, downloads, views, private_memory as privateMemory, working_set as workingSet, virtual_memory as virtualMemory, uploads, uncaught_exceptions as uncaughtExceptions
                    FROM statistics_general_minutes  
                    WHERE timestamp >= @startTime AND timestamp < @endTime;",
                new { startTime, endTime });

            if (minutesData.Count > 0)
            {
                var groupedData = minutesData.GroupBy(s => s.endpoint).Select(grp => grp.ToList()).ToList();
                foreach (var specificEndpointList in groupedData)
                {

                    var hourData = GetNewRequestData(specificEndpointList);
                    var timestamp = hourData.timestamp;

                    timestamp = startTime.RoundUp(TimeSpan.FromMinutes(60)).AddHours(-1);

                    hourData.timestamp = timestamp;

                    connection.Execute(
                        @"INSERT INTO statistics_hours(median, average, timestamp, countrequests, percentile95, percentile99, endpoint, render_time, db_exec_time) 
                            VALUES(@median, @average, @timestamp, @countrequests, @percentile95, @percentile99, @endpoint, @renderTime, @dbExecTime);",
                        new
                        {
                            median = hourData.median,
                            average = hourData.average,
                            timestamp = hourData.timestamp,
                            countrequests = hourData.countrequests,
                            percentile95 = hourData.percentile95,
                            percentile99 = hourData.percentile99,
                            endpoint = specificEndpointList[0].endpoint,
                            renderTime = hourData.renderTime,
                            dbExecTime = hourData.dbExecTime
                        });

                }
            }

            if (minutesGeneralData.Count > 0)
            {
                var timestamp = minutesGeneralData[0].timestamp;
                timestamp = startTime.RoundUp(TimeSpan.FromMinutes(60)).AddHours(-1);

                var generalData = GetNewGeneralData(minutesGeneralData);

                connection.Execute(
                    @"INSERT INTO statistics_general_hours(timestamp, downloads, views, private_memory, working_set, virtual_memory, uploads, uncaught_exceptions)
                        VALUES(@timestamp, @downloads, @views, @privateMemory, @workingSet, @virtualMemory, @uploads, @uncaughtExceptions)",
                    new {
                        timestamp, 
                        downloads = generalData.downloads, 
                        views = generalData.views, 
                        privateMemory = generalData.privateMemory, 
                        workingSet = generalData.workingSet, 
                        virtualMemory = generalData.virtualMemory,
                        uploads = generalData.uploads,
                        uncaughtExceptions = generalData.uncaughtExceptions

                    });
            }
        }

        public static void AddDayData(DateTime startTime)
        {
            using var connection = Database.OpenNewConnection();

            //round down to 00:00:00
            startTime = startTime.RoundUp(TimeSpan.FromHours(24)).AddDays(-1);

            var endTime = startTime.AddDays(1);

            var hoursData = (List<RequestData>)connection.Query<RequestData>(
                @"SELECT median, average, timestamp, countrequests, percentile95, percentile99, endpoint, render_time, db_exec_time as dbExecTime
                    FROM statistics_hours  
                    WHERE timestamp >= @startTime AND timestamp < @endTime;",
                new { startTime, endTime });

            var hoursGeneralData = (List<GeneralData>) connection.Query<GeneralData>(
                @"SELECT timestamp, downloads, views, private_memory as privateMemory, working_set as workingSet, virtual_memory as virtualMemory, uploads, uncaught_exceptions as uncaughtExceptions
                    FROM statistics_general_hours 
                    WHERE timestamp >= @startTime AND timestamp < @endTime;",
                new {startTime, endTime});

            if (hoursData.Count > 0)
            {
                var groupedData = hoursData.GroupBy(s => s.endpoint).Select(grp => grp.ToList()).ToList();
                foreach (var specificEndpointList in groupedData)
                {
                    var dayData = GetNewRequestData(specificEndpointList);
                    var timestamp = dayData.timestamp;

                    timestamp = timestamp.RoundUp(TimeSpan.FromHours(24)).AddDays(-1);

                    dayData.timestamp = timestamp;

                    connection.Execute(
                        @"INSERT INTO statistics_days(median, average, timestamp, countrequests, percentile95, percentile99, endpoint, render_time, db_exec_time) 
                            VALUES(@median, @average, @timestamp, @countrequests, @percentile95, @percentile99, @endpoint, @renderTime, @dbExecTime);",
                        new
                        {
                            median = dayData.median,
                            average = dayData.average,
                            timestamp = dayData.timestamp,
                            countrequests = dayData.countrequests,
                            percentile95 = dayData.percentile95,
                            percentile99 = dayData.percentile99,
                            endpoint = specificEndpointList[0].endpoint,
                            renderTime = dayData.renderTime,
                            dbExecTime = dayData.dbExecTime
                        });
                }
            }

            if (hoursGeneralData.Count > 0)
            {
                var timestamp = hoursGeneralData[0].timestamp;
                timestamp = startTime.RoundUp(TimeSpan.FromHours(24)).AddDays(-1);

                var generalData = GetNewGeneralData(hoursGeneralData);

                connection.Execute(
                    @"INSERT INTO statistics_general_days(timestamp, downloads, views, private_memory, working_set, virtual_memory, uploads, uncaught_exceptions)
                        VALUES(@timestamp, @downloads, @views, @privateMemory, @workingSet, @virtualMemory, @uploads, @uncaughtExceptions)",
                    new
                    {
                        timestamp, 
                        downloads = generalData.downloads, 
                        views = generalData.views,
                        privateMemory = generalData.privateMemory,
                        workingSet = generalData.workingSet,
                        virtualMemory = generalData.virtualMemory, 
                        uploads = generalData.uploads,
                        uncaughtExceptions = generalData.uncaughtExceptions
                    });
            }

            //Delete old data
            var dateMinutes = DateTime.UtcNow.AddMonths(-1);
            connection.ExecuteAsync(@"DELETE FROM statistics_general_minutes 
											WHERE timestamp < @dateMinutes;", 
										new { dateMinutes });
            connection.ExecuteAsync(@"DELETE FROM statistics_minutes 
											WHERE timestamp < @dateMinutes;",
		            new { dateMinutes });

            var dateHours = DateTime.UtcNow.AddMonths(-6);
            connection.ExecuteAsync(@"DELETE FROM statistics_general_hours
											WHERE timestamp < @dateHours;",
		            new { dateHours });
            connection.ExecuteAsync(@"DELETE FROM statistics_hours 
											WHERE timestamp < @dateHours;",
	            new { dateHours });


        }


        public static void AddDbExecTimeToRequest(HttpContext context, double time)
        {
            context.Items[DB_EXEC_TIME] = (float)context.Items[DB_EXEC_TIME] + (float)time;
        }

        public static void AddRenderTime(HttpContext context, double time)
        {
	        context.Items[RENDER_TIME] = (float)context.Items[RENDER_TIME] + (float)time;
        }

        private static GeneralData GetNewGeneralData(List<GeneralData> generalData)
        {
            var countDownloads = 0;
            long countViews = 0;
            long privateMemory = 0;
            long workingSet = 0;
            long virtualMemory = 0;
            var countUploads = 0;
            var countUncaughtExceptions = 0;

            foreach (var g in generalData)
            {
                countDownloads += g.downloads;
                countViews += g.views;
                privateMemory += g.privateMemory;
                workingSet += g.workingSet;
                virtualMemory += g.virtualMemory;
                countUploads += g.uploads;
                countUncaughtExceptions += g.uncaughtExceptions;
            }

            var result = new GeneralData
            {
                downloads = countDownloads,
                views = countViews,
                privateMemory = privateMemory / generalData.Count,
                workingSet = workingSet / generalData.Count,
                virtualMemory = virtualMemory / generalData.Count,
                uploads = countUploads,
                uncaughtExceptions = countUncaughtExceptions
            };
            return result;
        }

        private static RequestData GetNewRequestData(List<RequestData> requestData)
        {
            //Percentile
            float[] seconds95 = requestData.Select(c => c.percentile95).ToArray();
            float[] seconds99 = requestData.Select(c => c.percentile99).ToArray();
            var percentile95 = Percentile(seconds95, 0.95f);
            var percentile99 = Percentile(seconds99, 0.99f);

            requestData.Sort((r1, r2) => r1.median.CompareTo(r2.median));
            float median = 0;
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

            //countrequests
            long countrequests = 0;

            //Average and average render time and average db exec time
            float average = 0;
            float averageRenderTime = 0;
            float averageDbExecTime = 0;
            foreach (var req in requestData)
            {
                average += req.average;
                averageRenderTime += averageRenderTime;
                countrequests += req.countrequests;
                averageDbExecTime += req.dbExecTime;
            }
            average = average / requestData.Count;
            averageRenderTime = average / requestData.Count;
            averageDbExecTime = averageDbExecTime / requestData.Count;

            //timestamp
            var timestamp = requestData[requestData.Count / 2].timestamp;

            return new RequestData
            {
                median = median,
                average = average,
                timestamp = timestamp,
                countrequests = countrequests,
                percentile95 = percentile95,
                percentile99 = percentile99,
                renderTime =  averageRenderTime,
                dbExecTime =  averageDbExecTime
            };
        }

        private static float Percentile(float[] sequence, float excelPercentile)
        {
	        int N = sequence.Length;
	        float n = (N - 1) * excelPercentile + 1;
            // Another method: float n = (N + 1) * excelPercentile;
            if (n == 1d) return sequence[0];
            else if (n == N) return sequence[N - 1];
            else
            {
                int k = (int)n;
                float d = n - k;
                return sequence[k - 1] + d * (sequence[k] - sequence[k - 1]);
            }
        }

        private static string SerializeToBson(RequestInfo requestInfo)
        {
            return JsonSerializer.Serialize(requestInfo);
        }
    }

}
