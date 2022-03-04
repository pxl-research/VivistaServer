using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using tusdotnet;
using tusdotnet.Models;
using tusdotnet.Models.Configuration;
using tusdotnet.Stores;
using static VivistaServer.CommonController;


namespace VivistaServer
{
	public class Startup
	{
		private static Router router;

		public void ConfigureServices(IServiceCollection services)
		{
			services.Configure<FormOptions>(config => { config.MultipartBodyLengthLimit = long.MaxValue;});

			router = new Router();

			EmailClient.InitCredentials();

			HTMLRenderer.RegisterLayout(BaseLayout.Web, "Templates/base.liquid");

			CheckForFfmpeg();

			CreateDataDirectoryIfNeeded();

			RegisterGlobalExceptionLogger();
		}

		public void CheckForFfmpeg()
		{
			if (String.IsNullOrEmpty(Path.GetFullPath("ffmpeg")))
			{
				Console.WriteLine("ffmpeg executable not found in PATH");
				Environment.Exit(-1);
			}
		}

		public void CreateDataDirectoryIfNeeded()
		{
			Directory.CreateDirectory(VideoController.baseFilePath);
		}

		public void RegisterGlobalExceptionLogger()
		{
#if DEBUG
			AppDomain.CurrentDomain.FirstChanceException += ExceptionLogger;

			void ExceptionLogger(object source, FirstChanceExceptionEventArgs e)
			{
				Console.WriteLine(e.Exception.Message);
				Console.WriteLine(e.Exception.StackTrace);
			}
#endif
		}

		public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
		{
			if (env.IsDevelopment())
			{
				app.UseDeveloperExceptionPage();
				CommonController.baseURL = "https://localhost:5001";
			}
			else
			{
				app.UseHsts();
				CommonController.baseURL = "https://vivista.net";
			}

			CommonController.wwwroot = env.WebRootPath;
			app.UseStaticFiles();
			app.UseTus(context =>
			{
				if (!context.Request.Path.StartsWithSegments(new PathString("/api/file")) || context.Request.Method == "GET")
				{
					return null;
				}
				var guid = new Guid(context.Request.Headers["guid"]);
				string path = Path.Combine(VideoController.baseFilePath, guid.ToString());
				return new DefaultTusConfiguration
				{
					Store = new TusDiskStore(path),
					UrlPath = "/api/file",
					Events = new Events
					{
						OnAuthorizeAsync = VideoController.AuthorizeUploadTus,
						OnBeforeCreateAsync = async createContext => Directory.CreateDirectory(path),
						//NOTE(Simon): Do not allow deleting by someone trying to exploit the protocol
						OnBeforeDeleteAsync = async deleteContext => deleteContext.FailRequest(""),
						OnFileCompleteAsync = VideoController.ProcessUploadTus,
					}
				};
			});

			//Task.Run(PeriodicFunction);
            Task.Run(CollectData);

			app.Run(async (context) =>
            {

                var requestTime = Stopwatch.StartNew();

				var watch = Stopwatch.StartNew();
				var authenticateTask = UserSessions.GetLoggedInUser(context);

				PrintDebugData(context);

				SetJSONContentType(context);

				await authenticateTask;
				watch.Stop();
				CommonController.LogDebug($"request preamble: {watch.Elapsed.TotalMilliseconds} ms");

				await router.RouteAsync(context.Request, context);
                //TODO(Tom): Extract password, Ask Simon for help
				if (context.Request.HasFormContentType)
                {
                }

                var requestInfo = new RequestInfo
                {
					path = context.Request.Path,
					method = context.Request.Method,
					query = context.Request.Query,
					headers = context.Request.Headers,
					form = context.Request.HasFormContentType ? context.Request.Form : null

				};

                var request = new Request
                {
                    timestamp = DateTime.Now,
                    ms = requestTime.Elapsed.TotalMilliseconds,
					requestInfo =  requestInfo,
					endpoint = context.Request.Path.Value

                };

                DashboardController.AddRequestToCache(request);
            });
		}

		private void PrintDebugData(HttpContext context)
		{
#if DEBUG
			var watch = Stopwatch.StartNew();
			CommonController.LogDebug("Request data:");
			CommonController.LogDebug($"\tPath: {context.Request.Path}");
			CommonController.LogDebug($"\tMethod: {context.Request.Method}");
			CommonController.LogDebug("\tQuery: ");
			foreach (var kvp in context.Request.Query)
			{
				CommonController.LogDebug($"\t\t{kvp.Key}: {kvp.Value}");
			}
			CommonController.LogDebug("\tHeaders: ");
			foreach (var kvp in context.Request.Headers)
			{
				CommonController.LogDebug($"\t\t{kvp.Key}: {kvp.Value}");
			}
			if (!context.Request.HasFormContentType)
			{
				CommonController.LogDebug($"\tBody: {new StreamReader(context.Request.Body).ReadToEnd()}");
			}
			watch.Stop();
			CommonController.LogDebug($"writing debug info: {watch.Elapsed.TotalMilliseconds} ms");
#endif
		}

		private static async Task PeriodicFunction()
		{
			while (true)
			{
				Console.WriteLine("periodic");
				await Task.Delay(5000);
			}
		}

        private static async Task CollectData()
        {
            var lastHours = DateTime.Now;
            var lastDay = DateTime.Now;
            while (true)
            {
                await Task.Delay(60000);

				Task.Run(DashboardController.AddMinuteData);
				
                if (lastHours.Hour != DateTime.Now.Hour)
                {
                    Task.Run(() => DashboardController.AddHourData(lastHours));
                    lastHours = DateTime.Now;
				}

                if (lastDay.Day != DateTime.Now.Day)
                {
                    Task.Run(() => DashboardController.AddDayData(lastDay));
				}

            }
        }

	}
}
