using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace VivistaServer
{
	public class Program
	{
		public static void Main(string[] args)
		{
			CreateWebHostBuilder(args).Build().Run();
		}

		public static IWebHostBuilder CreateWebHostBuilder(string[] args)
		{
			return WebHost.CreateDefaultBuilder(args)
							.UseStartup<Startup>()
							.UseKestrel(options =>
							{
								options.AllowSynchronousIO = true;
							})
							.ConfigureLogging(options => {
								options.SetMinimumLevel(LogLevel.None);
								options.ClearProviders();
							});
		}
	}
}
