using System;
using System.Diagnostics;
using System.IO;
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
				if (!context.Request.Path.StartsWithSegments(new PathString("/api/file")))
				{
					return null;
				}
				var guid = new Guid(context.Request.Headers["guid"]);
				return new DefaultTusConfiguration
				{
					Store = new TusDiskStore(Path.Combine(VideoController.baseFilePath, guid.ToString())),
					UrlPath = "/api/file",
					Events = new Events
					{
						OnAuthorizeAsync = VideoController.AuthorizeUploadTus,
						//NOTE(Simon): Do not allow deleting by someone trying to exploit the protocol
						OnBeforeDeleteAsync = async deleteContext => deleteContext.FailRequest(""),
						OnFileCompleteAsync = VideoController.ProcessUploadTus,
					}
				};
			});

			//Task.Run(PeriodicFunction);

			app.Run(async (context) =>
			{
				var watch = Stopwatch.StartNew();
				var authenticateTask = UserSessions.GetLoggedInUser(context);

				PrintDebugData(context);

				SetJSONContentType(context);

				await authenticateTask;
				watch.Stop();
				Console.WriteLine($"request preamble: {watch.Elapsed.TotalMilliseconds} ms");

				await router.RouteAsync(context.Request, context);
			});
		}

		private void PrintDebugData(HttpContext context)
		{
#if DEBUG
			var watch = Stopwatch.StartNew();
			Console.WriteLine("Request data:");
			Console.WriteLine($"\tPath: {context.Request.Path}");
			Console.WriteLine($"\tMethod: {context.Request.Method}");
			Console.WriteLine("\tQuery: ");
			foreach (var kvp in context.Request.Query)
			{
				Console.WriteLine($"\t\t{kvp.Key}: {kvp.Value}");
			}
			Console.WriteLine("\tHeaders: ");
			foreach (var kvp in context.Request.Headers)
			{
				Console.WriteLine($"\t\t{kvp.Key}: {kvp.Value}");
			}
			if (!context.Request.HasFormContentType)
			{
				Console.WriteLine($"\tBody: {new StreamReader(context.Request.Body).ReadToEnd()}");
			}
			watch.Stop();
			Console.WriteLine($"writing debug info: {watch.Elapsed.TotalMilliseconds} ms");
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
	}
}
