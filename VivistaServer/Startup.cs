using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace VivistaServer
{
	public class Startup
	{
		public class Video
		{
			public Guid id;
			public int userid;
			public string username;
			public DateTime timestamp;
			public int downloadSize;

			public string title;
			public string description;
			public int length;
		}

		public class VideoResponse
		{
			public int totalcount;
			public int page;
			public int count;
			public IEnumerable<Video> videos;
		}

		private static PathString indexPath = new PathString("/");
		private static PathString registerPath = new PathString("/register");
		private static PathString loginPath = new PathString("/login");
		private static PathString videoPath = new PathString("/video");
		private static PathString metaPath = new PathString("/meta");
		private static PathString extraPath = new PathString("/extra");
		private static PathString allExtrasPath = new PathString("/extras");
		private static PathString thumbnailPath = new PathString("/thumbnail");

		private static int indexCountDefault = 10;

		//NOTE(Simon): Use GetPgsqlConfig(), it has caching.
		private static string connectionString;

		// This method gets called by the runtime. Use this method to add services to the container.
		// For more information on how to configure your application, visit https://go.microsoft.com/fwlink/?LinkID=398940
		public void ConfigureServices(IServiceCollection services)
		{

		}

		// This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
		public void Configure(IApplicationBuilder app, IHostingEnvironment env)
		{
			if (env.IsDevelopment())
			{
				app.UseDeveloperExceptionPage();
			}

			app.Run(async (context) =>
			{
				var path = context.Request.Path;
				bool isGet = context.Request.Method == "GET";
				bool isPost = context.Request.Method == "GET";

				context.Response.ContentType = "application/json";

				var connection = new NpgsqlConnection(GetPgsqlConfig());
				connection.Open();

				if (isGet)
				{
					if (MatchPath(path, indexPath))
					{
						await IndexGet(context, connection);
					}
					else if (MatchPath(path, videoPath))
					{
						await context.Response.WriteAsync("video get");
					}
					else if (MatchPath(path, metaPath))
					{
						await context.Response.WriteAsync("meta get");
					}
					else if (MatchPath(path, extraPath))
					{
						await context.Response.WriteAsync("extra get");
					}
					else if (MatchPath(path, allExtrasPath))
					{
						await context.Response.WriteAsync("extras get");
					}
					else if (MatchPath(path, thumbnailPath))
					{
						await context.Response.WriteAsync("thumbnail get");
					}
					else
					{
						context.Response.StatusCode = 404;
					}
				}
				else if (isPost)
				{
					if (MatchPath(path, registerPath))
					{
						await context.Response.WriteAsync("register post");
					}
					else if (MatchPath(path, loginPath))
					{
						await context.Response.WriteAsync("login post");
					}
					else if (MatchPath(path, videoPath))
					{
						await context.Response.WriteAsync("video post");
					}
					else if (MatchPath(path, metaPath))
					{
						await context.Response.WriteAsync("meta post");
					}
					else if (MatchPath(path, extraPath))
					{
						await context.Response.WriteAsync("extra post");
					}
					else if (MatchPath(path, allExtrasPath))
					{
						await context.Response.WriteAsync("extras post");
					}
					else if (MatchPath(path, thumbnailPath))
					{
						await context.Response.WriteAsync("thumbnail post");
					}
					else
					{
						context.Response.StatusCode = 404;
					}
				}
				else
				{
					context.Response.StatusCode = 404;
				}

				connection.Close();
			});
		}

		private async Task IndexGet(HttpContext context, NpgsqlConnection connection)
		{
			var args = context.Request.Query;
			if (!Int32.TryParse(args["offset"].ToString(), out int offset) || offset < 0)
			{
				offset = 0;
			}

			if (!Int32.TryParse(args["count"].ToString(), out int count) || count > 100 || count < 0)
			{
				count = indexCountDefault;
			}

			string author = args["author"];
			int? userid = null;

			if (!String.IsNullOrEmpty(author))
			{
				userid = await GetUserId(author, connection);
			}

			DateTime? uploadDate;
			if (!Int32.TryParse(args["agedays"].ToString(), out int daysOld) || daysOld < 0)
			{
				uploadDate = null;
			}
			else
			{
				uploadDate = DateTime.UtcNow.AddDays(-daysOld);
			}

			var videos = new VideoResponse();

			try
			{
				//TODO(Simon): There might be a faster way to get the count, while also executing just 1 query: add "count(*) OVER() AS total_count" to query
				videos.totalcount = await connection.QuerySingleAsync<int>(@"select count(*) from videos v
								inner join users u on v.userid = u.userid
								where (@userid::int is NULL or v.userid=$1)
								and (@uploadDate::timestamp is NULL or v.timestamp>=$2)", new { userid, uploadDate});
				videos.videos = await connection.QueryAsync<Video>(@"select v.id, v.userid, u.username, v.timestamp, v.downloadsize, v.title, v.description, v.length from videos v
								inner join users u on v.userid = u.userid
								where (@userid::int is NULL or v.userid=$1)
								and (@uploadDate::timestamp is NULL or v.timestamp>=$2)
								order by v.timestamp desc
								limit @count
								offset @offset", new { userid, uploadDate, count, offset });

				videos.count = videos.videos.Count();
				videos.page = videos.totalcount > 0 ? offset / videos.totalcount + 1 : 1;
			}
			catch (Exception e)
			{
				await WriteError(context, "Something went wrong while processing this request", 500, e);
				return;
			}

			await context.Response.Body.WriteAsync(Utf8Json.JsonSerializer.SerializeUnsafe(videos));
		}

		private static string GetPgsqlConfig()
		{
			if (string.IsNullOrEmpty(connectionString))
			{
				var host = Environment.GetEnvironmentVariable("360VIDEO_DB_HOST");
				if (String.IsNullOrEmpty(host))
				{
					host = "localhost";
				}

				var user = Environment.GetEnvironmentVariable("360VIDEO_DB_USER");
				if (String.IsNullOrEmpty(user))
				{
					user = "postgres";
				}

				var password = Environment.GetEnvironmentVariable("360VIDEO_DB_PASSWORD");
				if (String.IsNullOrEmpty(password))
				{
					password = Environment.GetEnvironmentVariable("USER");
				}

				var database = "360video";

				connectionString = $"Server={host};Port=5432;Database={database};User Id={user};Password={password}";
			}

			return connectionString;
		}

		private static bool MatchPath(PathString fullPath, PathString startSegment)
		{
			return fullPath.StartsWithSegments(startSegment, StringComparison.OrdinalIgnoreCase);
		}

		private static async Task<int?> GetUserId(string username, IDbConnection connection)
		{
			int? id = await connection.QueryFirstOrDefaultAsync<int?>("select userid from users where username = @username", new { username });
			return id == 0 ? null : id;
		}

		private static async Task WriteError(HttpContext context, string error, int errorCode, Exception e = null)
		{
			context.Response.StatusCode = errorCode;
			await context.Response.WriteAsync($"{{\"error\": \"{error}\"}}");
			#if DEBUG
			if (e != null)
			{
				await context.Response.WriteAsync(e.ToString());
			}
			#endif
		}
	}
}
