using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Fluid;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http;
using Npgsql;
using static VivistaServer.CommonController;

namespace VivistaServer
{
    public class Request
    {
        public DateTime timestamp;
        public double ms;
        public RequestInfo requestInfo;
    }

    public class RequestData
    {
        public double median;
        public double average;
        public DateTime timestamp;
        public int countrequests;
    }


    public class RequestInfo
    {
        public PathString path;
        public string method;
        public IQueryCollection query;
        public IHeaderDictionary headers;
        public Stream body;
    }
    public class DashboardController
    {

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




            Task.WaitAll( userTask, videoTask, downloadTask);


            var templateContext = new TemplateContext(new { users = userTask.Result, videos = videoTask.Result, downloads = downloadTask.Result });

            await context.Response.WriteAsync(await HTMLRenderer.Render(context, "Templates\\dashboard.liquid", templateContext));
        }




        public static void AddRequestToCache(Request request)
        {
            cachedRequests.Add(request);
        }

        public static void AddMinuteData()
        {
            if (cachedRequests.Count > 0)
            {
                //Median
                cachedRequests.Sort((r1, r2) => r1.ms.CompareTo(r2.ms));
                double median = 0;
                if (cachedRequests.Count % 2 == 1)
                {
                    median = cachedRequests[cachedRequests.Count / 2].ms;
                }
                else
                {
                    var firstValue = cachedRequests[cachedRequests.Count / 2 - 1].ms;
                    var secondValue = cachedRequests[cachedRequests.Count / 2].ms;
                    median = (firstValue + secondValue) / 2;
                }

                //Average
                var average = cachedRequests.Average((req) => req.ms);

                //timestamp
                var timestamp = cachedRequests[cachedRequests.Count / 2].timestamp;
                //timestamp = timestamp.AddSeconds(-timestamp.Second);
                //timestamp = timestamp.AddMilliseconds(-timestamp.Millisecond);
                timestamp = timestamp.AddTicks(-(timestamp.Ticks % 1000000000));


                using var connection = Database.OpenNewConnection();
                connection.Execute(
                    @"insert into statistics_minutes(median, average, timestamp, countrequests) values(@median, @average, @timestamp, @countrequests);", new {median, average, timestamp, countrequests  = cachedRequests.Count});

                //Outliers
                foreach (var req in cachedRequests)
                {
                    if (req.ms > average * 4)
                    {
                        connection.Execute(@"insert into statistics_outliers(timestamp, ms) values(@timestamp, @ms);", 
                            new { timestamp = req.timestamp, ms = req.timestamp });
                    }
                }
                cachedRequests.Clear();
            }

        }

        public static void AddHourData(DateTime startTime)
        {
            var connection = Database.OpenNewConnection();

            //startTime = startTime.AddMinutes(-startTime.Minute);
            //startTime = startTime.AddSeconds(-startTime.Second);
            //startTime = startTime.AddMilliseconds(-startTime.Millisecond);
            startTime = startTime.AddTicks(-(startTime.Ticks % 100000000000));

            var endTime = startTime.AddHours(1);

            var minutesData = (List<RequestData>) connection.Query<RequestData>(
                @"SELECT median, average, timestamp, countrequests 
                    FROM statistics_minutes  
                    WHERE timestamp >= @startTime AND timestamp < @endTime;", 
                new {startTime, endTime});

            if (minutesData.Count > 0)
            {
                var hourData = GetMedianAverageTimestamp(minutesData);
                var timestamp = hourData.timestamp;

                timestamp = timestamp.AddMinutes(-timestamp.Minute);
                timestamp = timestamp.AddSeconds(-timestamp.Second);
                timestamp = timestamp.AddMilliseconds(-timestamp.Millisecond);
                timestamp = timestamp.AddTicks(-(timestamp.Ticks % 10000000));

                hourData.timestamp = timestamp;

                connection.Execute(
                    @"insert into statistics_hours(median, average, timestamp, countrequests) values(@median, @average, @timestamp, @countrequests);", 
                    new {median = hourData.median, average = hourData.average, timestamp = hourData.timestamp, countrequests = hourData.countrequests});
            }


        }

        public static void AddDayData(DateTime startTime)
        {
            var connection = Database.OpenNewConnection();
            startTime = startTime.AddHours(-startTime.Hour);
            startTime = startTime.AddMinutes(-startTime.Minute);
            startTime = startTime.AddSeconds(-startTime.Second);
            startTime = startTime.AddMilliseconds(-startTime.Millisecond);
            startTime = startTime.AddTicks(-(startTime.Ticks % 10000000));

            var endTime = startTime.AddDays(1);

            var hoursData = (List<RequestData>) connection.Query<RequestData>(@"SELECT median, average, timestamp FROM hours  WHERE timestamp >= @startTime AND timestamp < @endTime;", new { startTime, endTime });

            if (hoursData.Count > 0)
            {

                var dayData = GetMedianAverageTimestamp(hoursData);
                var timestamp = dayData.timestamp;

                timestamp = timestamp.AddHours(-timestamp.Hour);
                timestamp = timestamp.AddMinutes(-timestamp.Minute);
                timestamp = timestamp.AddSeconds(-timestamp.Second);
                timestamp = timestamp.AddMilliseconds(-timestamp.Millisecond);
                timestamp = timestamp.AddTicks(-(timestamp.Ticks % 10000000));

                dayData.timestamp = timestamp;


                connection.Execute(
                    @"insert into statistics_days(median, average, timestamp, countrequests) values(@median, @average, @timestamp, @countrequests);", new {median = dayData.median, average = dayData.average, timestamp = dayData.timestamp, countrequests = dayData.countrequests});
            }
        }


        private static RequestData GetMedianAverageTimestamp(List<RequestData> requestData)
        {
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
            var average = requestData.Average((req) => req.average);

            //timestamp
            var timestamp = requestData[requestData.Count / 2].timestamp;

            var countrequests = requestData.Sum(req => req.countrequests);

            return new RequestData
            {
                median =  median,
                average = average,
                timestamp = timestamp,
                countrequests =  countrequests
            };
        }


    }

}
