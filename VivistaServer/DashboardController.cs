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
		public string endpoint { get; set; }
		public float median { get; set; }
		public float average { get; set; }
		public DateTime timestamp { get; set; }
		public float percentile95 { get; set; }
		public float percentile99 { get; set; }
		public long countrequests { get; set; }
		public float renderTime { get; set; }
		public float dbExecTime { get; set; }
	}

	public class GeneralData
	{
		public DateTime timestamp { get; set; }
		public int downloads { get; set; }
		public long views { get; set; }
		public long privateMemory { get; set; }
		public long workingSet { get; set; }
		public long virtualMemory { get; set; }
		public int uploads { get; set; }
		public int uncaughtExceptions { get; set; }
		public long countTotalRequests { get; set; }
		public int countItemsUserCache { get; set; }
		public int countItemsUploadCache { get; set; }
	}

	public class EndpointPercentile
	{
		public float percentile99;
		public string endpoint;
	}

	//NOTE(Tom): Needs to be in a separate class for serialization to bson
	public class RequestInfo
	{
		public QueryCollection query { get; set; }
		public FormCollection form { get; set; }
	}

	public class Outlier
	{
		public DateTime timestamp { get; set; }
		public float seconds { get; set; }
		public string reqinfo { get; set; }
		public string endpoint { get; set; }
	}

	public class DashboardController
	{
		public const string DB_EXEC_TIME = "dbExecTime";
		public const string RENDER_TIME = "renderTime";

		public static int downloads;
		private static int views;
		private static int uploads;
		private static int uncaughtExceptions;
		private static List<Request> cachedRequests = new List<Request>();

		private static readonly Object cachedRequestLock = new Object();

		[Route("GET", "/admin/dashboard")]
		private static async Task DashboardGet(HttpContext context)
		{
			using var connection = Database.OpenNewConnection();
			if (await User.IsUserAdmin(context, connection))
			{
				string searchQuery = context.Request.Query["date"].ToString();
				var date = DateTime.UtcNow;
				if (!string.IsNullOrEmpty(searchQuery))
				{
					try
					{
						date = Convert.ToDateTime(searchQuery);
					}
					catch (Exception ex)
					{
						CommonController.LogDebug(ex.Message);
					}
				}

				SetHTMLContentType(context);
				//TODO: Multiply database connections
				var userTask = Task.Run(async () =>
				{
					using var connection = Database.OpenNewConnection();
					return await Database.QueryAsync<int>(connection, "SELECT COUNT(*) FROM users;", context);
				});

				var videoTask = Task.Run(async () =>
				{
					using var connection = Database.OpenNewConnection();
					return await Database.QueryAsync<int>(connection, "SELECT COUNT(*) FROM videos;", context);
				});

				var downloadTask = Task.Run(async () =>
				{
					using var connection = Database.OpenNewConnection();
					return await Database.QueryAsync<int>(connection, "SELECT COALESCE(SUM(downloads), 0) FROM videos;", context);
				});

				var startDateMinute = new DateTime(date.Year, date.Month, date.Day);
				var endDateMinute = startDateMinute.AddDays(1);
				var minuteData = Task.Run(async () =>
				{
					using var connection = Database.OpenNewConnection();
					return await Database.QueryAsync<RequestData>(connection,
						@"SELECT median, average, timestamp, countrequests, percentile95, percentile99, endpoint, render_time as renderTime, db_exec_time as dbExecTime 
							FROM statistics_minutes 
							WHERE @startDate <= timestamp AND @endDate > timestamp 
							ORDER BY timestamp", context, new { startDate = startDateMinute, endDate = endDateMinute });
				});

				var serverRestart = Task.Run(async () =>
				{
					using var connection = Database.OpenNewConnection();
					return await Database.QueryAsync<DateTime>(connection,
						@"SELECT timestamp
							FROM server_restart
							WHERE @startDate <= timestamp AND @endDate > timestamp 
							ORDER BY timestamp", context, new { startDate = startDateMinute, endDate = endDateMinute });
				});

				var hourData = Task.Run(async () =>
				{
					var startDate = date.RoundUp(TimeSpan.FromHours(24)).AddDays(-(int)date.DayOfWeek);
					var endDate = startDate.AddDays(7);
					using var connection = Database.OpenNewConnection();
					return await Database.QueryAsync<RequestData>(connection,
						@"SELECT median, average, timestamp, countrequests, percentile95, percentile99, endpoint, render_time as renderTime, db_exec_time as dbExecTime 
							FROM statistics_hours 
							WHERE @startDate <= timestamp AND @endDate > timestamp 
							ORDER BY timestamp", context, new { startDate, endDate });
				});
				var dayData = Task.Run(async () =>
				{
					var startDate = new DateTime(date.Year, date.Month, 1);
					var endDate = startDate.AddMonths(1);
					using var connection = Database.OpenNewConnection();
					return await Database.QueryAsync<RequestData>(connection,
						@"SELECT median, average, timestamp, countrequests, percentile95, percentile99, endpoint, render_time as renderTime, db_exec_time as dbExecTime 
							FROM statistics_days 
							WHERE @startDate <= timestamp AND @endDate > timestamp 
							ORDER BY timestamp", context, new { startDate, endDate });
				});

				var generalMinuteData = Task.Run(async () =>
				{
					var startDate = new DateTime(date.Year, date.Month, date.Day);
					var endDate = startDate.AddDays(1);
					using var connection = Database.OpenNewConnection();
					return await Database.QueryAsync<GeneralData>(connection,
						@"SELECT timestamp, downloads, views, private_memory as privateMemory, working_set as workingSet, virtual_memory as virtualMemory, uploads, uncaught_exceptions as uncaughtExceptions, count_total_requests as countTotalRequests, count_items_user_cache as countItemsUserCache, count_items_upload_cache as countItemsUploadCache 
							FROM statistics_general_minutes 
							WHERE @startDate <= timestamp AND @endDate > timestamp 
							ORDER BY timestamp", context, new { startDate, endDate });
				});

				var generalHourData = Task.Run(async () =>
				{
					var startDate = date.RoundUp(TimeSpan.FromHours(24)).AddDays(-(int)date.DayOfWeek);
					var endDate = startDate.AddDays(7);
					using var connection = Database.OpenNewConnection();
					return await Database.QueryAsync<GeneralData>(connection,
						@"SELECT timestamp, downloads, views, private_memory as privateMemory, working_set as workingSet, virtual_memory as virtualMemory, uploads, uncaught_exceptions as uncaughtExceptions, count_total_requests as countTotalRequests,count_items_user_cache as countItemsUserCache, count_items_upload_cache as countItemsUploadCache  
							FROM statistics_general_hours 
							WHERE @startDate <= timestamp AND @endDate > timestamp 
							ORDER BY timestamp", context, new { startDate, endDate });
				});

				var generalDayData = Task.Run(async () =>
				{
					var startDate = new DateTime(date.Year, date.Month, 1);
					var endDate = startDate.AddMonths(1);
					using var connection = Database.OpenNewConnection();
					return await Database.QueryAsync<GeneralData>(connection,
						@"SELECT timestamp, downloads, views, private_memory as privateMemory, working_set as workingSet, virtual_memory as virtualMemory, uploads, uncaught_exceptions as uncaughtExceptions, count_total_requests as countTotalRequests, count_items_user_cache as countItemsUserCache, count_items_upload_cache as countItemsUploadCache 
							FROM statistics_general_days 
							WHERE @startDate <= timestamp AND @endDate > timestamp 
							ORDER BY timestamp", context, new { startDate, endDate });
				});

				var endpoints = Startup.GetEndpointsOfRoute();

				var outliers = Task.Run(async () =>
				{
					var startDate = new DateTime(date.Year, date.Month, 1);
					var endDate = startDate.AddMonths(1);
					using var connection = Database.OpenNewConnection();
					return await Database.QueryAsync<Outlier>(connection,
						@"SELECT timestamp, seconds, endpoint, reqinfo 
							FROM statistics_outliers 
							WHERE @startDate <= timestamp AND @endDate > timestamp 
							ORDER BY timestamp", context, new { startDate, endDate });
				});

				Task.WaitAll(userTask, videoTask, downloadTask, minuteData, hourData, dayData, generalDayData, generalHourData, generalMinuteData, outliers);

				var templateContext = new TemplateContext(new
				{
					users = userTask.Result,
					videos = videoTask.Result,
					downloads = downloadTask.Result,
					endpoints = endpoints,
					averagesMinutes = minuteData.Result.Select(s => new { x = s.timestamp, y = s.average, s.endpoint }),
					minuteData = minuteData.Result,
					hourData = hourData.Result,
					dayData = dayData.Result,
					generalMinuteData = generalMinuteData.Result,
					generalHourData = generalHourData.Result,
					generalDayData = generalDayData.Result,
					outliers = outliers.Result,
					countOutliers = outliers.Result.Count(),
					serverRestart = serverRestart.Result
				});
				await context.Response.WriteAsync(await HTMLRenderer.Render(context, "Templates\\dashboard.liquid", templateContext));
			}
			else
			{
				await CommonController.Write404(context);
			}
		}



		[Route("GET", "/admin/outliers")]
		private static async Task OutliersPost(HttpContext context)
		{
			SetHTMLContentType(context);
			await context.Response.WriteAsync(await HTMLRenderer.Render(context, "Templates\\outliers.liquid", new TemplateContext { }));
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
			int count = cachedRequests.Count;
			using var transaction = connection.BeginTransaction();
			if (count > 0)
			{
				//Note(Tom): Add tempOutliers so lock can close fast
				var tempOutliers = new List<Request>();
				List<List<Request>> groupedRequests;

				lock (cachedRequestLock)
				{
					groupedRequests = cachedRequests.GroupBy(s => s.endpoint).Select(grp => grp.ToList()).ToList();
					var percentilesPerEndpoint = connection.Query<EndpointPercentile>(
						@"SELECT avg(percentile99) as percentile99, endpoint
						FROM statistics_days
						GROUP BY endpoint;", null,transaction)
						.ToDictionary(p => p.endpoint, p => p.percentile99);

					foreach (var req in cachedRequests)
					{
						float outlierThreshold = percentilesPerEndpoint.ContainsKey(req.endpoint)
							? percentilesPerEndpoint[req.endpoint] * 2f
							: float.MaxValue;

						if (req.seconds > outlierThreshold)
						{
							tempOutliers.Add(req);
						}
					}
					cachedRequests.Clear();
				}

				foreach (var req in tempOutliers)
				{
					string reqinfo = SerializeToBson(req.requestInfo);
					connection.Execute(@"insert into statistics_outliers(timestamp, seconds, reqinfo, endpoint) 
										values(@timestamp, @seconds, @reqinfo::jsonb, @endpoint);",
										new { req.timestamp, req.seconds, reqinfo, req.endpoint }, transaction);
				}



				var timestamp = groupedRequests[0][0].timestamp;
				//round down to xx:xx:00
				timestamp = timestamp.RoundUp(TimeSpan.FromSeconds(60)).AddMinutes(-1);

				foreach (var specificEndpointList in groupedRequests)
				{
					specificEndpointList.Sort((r1, r2) => r1.seconds.CompareTo(r2.seconds));
					float[] seconds = specificEndpointList.Select(c => c.seconds).ToArray();
					float percentile95 = Percentile(seconds, 0.95f);
					float percentile99 = Percentile(seconds, 0.99f);

					float median = 0;
					if (specificEndpointList.Count % 2 == 1)
					{
						median = specificEndpointList[specificEndpointList.Count / 2].seconds;
					}
					else
					{
						float firstValue = specificEndpointList[specificEndpointList.Count / 2 - 1].seconds;
						float secondValue = specificEndpointList[specificEndpointList.Count / 2].seconds;
						median = (firstValue + secondValue) / 2;
					}

					float average = 0;
					float averageRenderTime = 0;
					float averageDbExecTime = 0;
					foreach (var req in specificEndpointList)
					{
						average += req.seconds;
						averageRenderTime += req.renderTime;
						averageDbExecTime += req.dbExecTime;
					}

					average /= specificEndpointList.Count;
					averageRenderTime = average / specificEndpointList.Count;
					averageDbExecTime /= specificEndpointList.Count;

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
								specificEndpointList[0].endpoint,
								renderTime = averageRenderTime,
								dbExecTime = averageDbExecTime
							});
				}

				Process.GetCurrentProcess().Refresh();
				long privateMemory = Process.GetCurrentProcess().PrivateMemorySize64;
				long workingSet = Process.GetCurrentProcess().WorkingSet64;
				long virtualMemory = Process.GetCurrentProcess().VirtualMemorySize64;
				int countUserCache = UserSessions.GetItemsInUserCache();
				int countUploadCache = VideoController.GetItemsInUploadCache();

				connection.Execute(
					@"INSERT INTO statistics_general_minutes(timestamp, downloads, views, private_memory, working_set, virtual_memory, uploads, uncaught_exceptions, count_total_requests, count_items_user_cache, count_items_upload_cache)
						VALUES(@timestamp, @downloads, @views, @privateMemory, @workingSet, @virtualMemory, @uploads, @uncaughtExceptions, @countTotalRequests, @countUserCache, @countUploadCache);",
						new
						{
							timestamp,
							downloads,
							views,
							privateMemory,
							workingSet,
							virtualMemory,
							uploads,
							uncaughtExceptions,
							countTotalRequests = count,
							countUserCache,
							countUploadCache
						}, transaction);

				downloads = 0;
				views = 0;
				uploads = 0;
				uncaughtExceptions = 0;
			}
			transaction.Commit();
		}

		public static void AddHourData(DateTime startTime)
		{
			using var connection = Database.OpenNewConnection();
			using var transaction = connection.BeginTransaction();
			//NOTE(Tom): round down to xx:00:00
			startTime = startTime.RoundUp(TimeSpan.FromMinutes(60)).AddHours(-1);

			var endTime = startTime.AddHours(1);

			var minutesData = (List<RequestData>)connection.Query<RequestData>(
				@"SELECT median, average, timestamp, countrequests, percentile95, percentile99, endpoint, render_time as renderTime, db_exec_time as dbExecTime
					FROM statistics_minutes  
					WHERE timestamp >= @startTime AND timestamp < @endTime;",
					new { startTime, endTime }, transaction);

			var minutesGeneralData = (List<GeneralData>)connection.Query<GeneralData>(
				@"SELECT timestamp, downloads, views, private_memory as privateMemory, working_set as workingSet, virtual_memory as virtualMemory, uploads, uncaught_exceptions as uncaughtExceptions, count_total_requests as countTotalRequests, count_items_user_cache as countItemsUserCache, count_items_upload_cache as countItemsUploadCache
					FROM statistics_general_minutes  
					WHERE timestamp >= @startTime AND timestamp < @endTime;",
					new { startTime, endTime }, transaction);

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
								hourData.median,
								hourData.average,
								hourData.timestamp,
								hourData.countrequests,
								hourData.percentile95,
								hourData.percentile99,
								specificEndpointList[0].endpoint,
								hourData.renderTime,
								hourData.dbExecTime
							}, transaction);
				}
			}

			if (minutesGeneralData.Count > 0)
			{
				var timestamp = minutesGeneralData[0].timestamp;
				timestamp = startTime.RoundUp(TimeSpan.FromMinutes(60)).AddHours(-1);

				var generalData = GetNewGeneralData(minutesGeneralData);

				connection.Execute(
					@"INSERT INTO statistics_general_hours(timestamp, downloads, views, private_memory, working_set, virtual_memory, uploads, uncaught_exceptions, count_total_requests, count_items_user_cache, count_items_upload_cache)
						VALUES(@timestamp, @downloads, @views, @privateMemory, @workingSet, @virtualMemory, @uploads, @uncaughtExceptions, @countTotalRequests, @countItemsUserCache ,@countItemsUploadCache)",
						new
						{
							timestamp,
							generalData.downloads,
							generalData.views,
							generalData.privateMemory,
							generalData.workingSet,
							generalData.virtualMemory,
							generalData.uploads,
							generalData.uncaughtExceptions,
							generalData.countTotalRequests,
							generalData.countItemsUserCache,
							generalData.countItemsUploadCache

						}, transaction);
			}
			transaction.Commit();
		}

		public static void AddDayData(DateTime startTime)
		{
			using var connection = Database.OpenNewConnection();
			using var transaction = connection.BeginTransaction();
			//NOTE(Tom): round down to 00:00:00
			startTime = startTime.RoundUp(TimeSpan.FromHours(24)).AddDays(-1);

			var endTime = startTime.AddDays(1);

			var hoursData = (List<RequestData>)connection.Query<RequestData>(
				@"SELECT median, average, timestamp, countrequests, percentile95, percentile99, endpoint, render_time as renderTime, db_exec_time as dbExecTime
					FROM statistics_hours  
					WHERE timestamp >= @startTime AND timestamp < @endTime;",
				new { startTime, endTime }, transaction);

			var hoursGeneralData = (List<GeneralData>)connection.Query<GeneralData>(
				@"SELECT timestamp, downloads, views, private_memory as privateMemory, working_set as workingSet, virtual_memory as virtualMemory, uploads, uncaught_exceptions as uncaughtExceptions, count_total_requests as countTotalRequests, count_items_user_cache as countItemsUserCache, count_items_upload_cache as countItemsUploadCache
					FROM statistics_general_hours 
					WHERE timestamp >= @startTime AND timestamp < @endTime;",
				new { startTime, endTime }, transaction);

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
							dayData.median,
							dayData.average,
							dayData.timestamp,
							dayData.countrequests,
							dayData.percentile95,
							dayData.percentile99,
							specificEndpointList[0].endpoint,
							dayData.renderTime,
							dayData.dbExecTime
						}, transaction);
				}
			}

			if (hoursGeneralData.Count > 0)
			{
				var timestamp = hoursGeneralData[0].timestamp;
				timestamp = startTime.RoundUp(TimeSpan.FromHours(24)).AddDays(-1);

				var generalData = GetNewGeneralData(hoursGeneralData);

				connection.Execute(
					@"INSERT INTO statistics_general_days(timestamp, downloads, views, private_memory, working_set, virtual_memory, uploads, uncaught_exceptions, count_total_requests, count_items_user_cache, count_items_upload_cache)
						VALUES(@timestamp, @downloads, @views, @privateMemory, @workingSet, @virtualMemory, @uploads, @uncaughtExceptions, @countTotalRequests, @countItemsUserCache, @countItemsUploadCache)",
					new
					{
						timestamp,
						generalData.downloads,
						generalData.views,
						generalData.privateMemory,
						generalData.workingSet,
						generalData.virtualMemory,
						generalData.uploads,
						generalData.uncaughtExceptions,
						generalData.countTotalRequests,
						generalData.countItemsUserCache,
						generalData.countItemsUploadCache
					}, transaction);
			}

			//NOTE(Tom): Delete old data
			var dateMinutes = DateTime.UtcNow.AddMonths(-1);
			connection.ExecuteAsync(@"DELETE FROM statistics_general_minutes 
										WHERE timestamp < @dateMinutes;",
										new { dateMinutes }, transaction);
			connection.ExecuteAsync(@"DELETE FROM statistics_minutes 
										WHERE timestamp < @dateMinutes;",
										new { dateMinutes }, transaction);

			var dateHours = DateTime.UtcNow.AddMonths(-6);
			connection.ExecuteAsync(@"DELETE FROM statistics_general_hours
										WHERE timestamp < @dateHours;",
										new { dateHours }, transaction);
			connection.ExecuteAsync(@"DELETE FROM statistics_hours 
										WHERE timestamp < @dateHours;",
										new { dateHours }, transaction);

			transaction.Commit();
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
			int countDownloads = 0;
			long countViews = 0;
			long privateMemory = 0;
			long workingSet = 0;
			long virtualMemory = 0;
			int countUploads = 0;
			int countUncaughtExceptions = 0;
			long totalRequests = 0;
			int countItemsUserCache = 0;
			int countItemsUploadCache = 0;

			foreach (var g in generalData)
			{
				countDownloads += g.downloads;
				countViews += g.views;
				privateMemory += g.privateMemory;
				workingSet += g.workingSet;
				virtualMemory += g.virtualMemory;
				countUploads += g.uploads;
				countUncaughtExceptions += g.uncaughtExceptions;
				totalRequests += g.countTotalRequests;
				countItemsUserCache += g.countItemsUserCache;
				countItemsUploadCache += g.countItemsUploadCache;
			}

			var result = new GeneralData
			{
				downloads = countDownloads,
				views = countViews,
				privateMemory = privateMemory / generalData.Count,
				workingSet = workingSet / generalData.Count,
				virtualMemory = virtualMemory / generalData.Count,
				uploads = countUploads,
				uncaughtExceptions = countUncaughtExceptions,
				countTotalRequests = totalRequests,
				countItemsUserCache = countItemsUserCache,
				countItemsUploadCache = countItemsUploadCache,
			};
			return result;
		}

		private static RequestData GetNewRequestData(List<RequestData> requestData)
		{
			float[] seconds95 = requestData.Select(c => c.percentile95).ToArray();
			float[] seconds99 = requestData.Select(c => c.percentile99).ToArray();
			float percentile95 = Percentile(seconds95, 0.95f);
			float percentile99 = Percentile(seconds99, 0.99f);

			requestData.Sort((r1, r2) => r1.median.CompareTo(r2.median));
			float median = 0;
			if (requestData.Count % 2 == 1)
			{
				median = requestData[requestData.Count / 2].median;
			}
			else
			{
				float firstValue = requestData[requestData.Count / 2 - 1].median;
				float secondValue = requestData[requestData.Count / 2].median;
				median = (firstValue + secondValue) / 2;
			}

			long countrequests = 0;

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
			average /= requestData.Count;
			averageRenderTime = average / requestData.Count;
			averageDbExecTime /= requestData.Count;

			var timestamp = requestData[requestData.Count / 2].timestamp;

			return new RequestData
			{
				median = median,
				average = average,
				timestamp = timestamp,
				countrequests = countrequests,
				percentile95 = percentile95,
				percentile99 = percentile99,
				renderTime = averageRenderTime,
				dbExecTime = averageDbExecTime
			};
		}

		//Note(Tom): Array needs to be sorted
		private static float Percentile(float[] values, float percentile)
		{
			int count = values.Length;
			float desiredIndex = (count - 1) * percentile + 1;

			if (values.Length == 1)
			{
				return values[0];
			}

			int realIndex = (int)desiredIndex;
			float t = desiredIndex % realIndex;
			return Lerp(values[realIndex - 1], values[realIndex], t);
		}

		private static float Lerp(float a, float b, float t)
		{
			return a + (b - a) * t;
		}

		private static string SerializeToBson(RequestInfo requestInfo)
		{
			return JsonSerializer.Serialize(requestInfo);
		}
	}
}
