using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Fluid;
using Microsoft.AspNetCore.Http;

namespace VivistaServer
{
	public class InstallController
	{
		private static string playerFolder = Path.Combine(CommonController.wwwroot, "Installers", "Player");
		private static string editorFolder = Path.Combine(CommonController.wwwroot, "Installers", "Editor");

		private static string cachedVersionNumber;
		private static DateTime cacheTime;
		private static TimeSpan maxCacheTime = new TimeSpan(0, 10, 0);

		//NOTE(Simon): The number is not actually that important. Client-side we just want to check if the number is the same or not. If not, suggest update to user.
		[Route("GET", "/api/latest_version_number")]
		[Route("GET", "/api/v1/latest_version_number")]
		private static async Task LatestVersionNumberGet(HttpContext context)
		{
			string version;
			//NOTE(Simon): Cache time still valid
			if (cacheTime > DateTime.UtcNow - maxCacheTime)
			{
				version = cachedVersionNumber;
			}
			else
			{
				var installerName = new DirectoryInfo(playerFolder)
					.GetFiles("*.exe")
					.OrderBy(x => x.LastWriteTime)
					.First().Name;
				//NOTE(Simon): String looks like "VivistaPlayer-x.x.x.exe. So we take the substring from the first dash to the last period.
				var lastPart = installerName.Substring(installerName.IndexOf('-') + 1);
				version = lastPart.Substring(0, lastPart.LastIndexOf('.'));
				cachedVersionNumber = version;
				cacheTime = DateTime.UtcNow;
			}

			await context.Response.WriteAsJsonAsync(new {version});
		}

		[Route("GET", "/install")]
		private static async Task InstallGet(HttpContext context)
		{
			CommonController.SetHTMLContentType(context);
			var playerURL = GetLatestPlayerInstallerURL();
			var editorURL = GetLatestEditorInstallerURL();

			var templateContext = new TemplateContext(new { playerURL, editorURL });
			await context.Response.WriteAsync(await HTMLRenderer.Render(context, "Templates\\install.liquid", templateContext));

		}

		[Route("GET", "/install/latest")]
		private static async Task InstallLatestGet(HttpContext context)
		{
			context.Response.ContentType = "application/octet-stream";
			
			context.Response.Redirect(GetLatestPlayerInstallerURL());
		}

		[Route("GET", "/api/latest_version_player_url")]
		[Route("GET", "/api/v1/latest_version_player_url")]
		private static async Task LatestPlayerURLGet(HttpContext context)
		{
			var url = GetLatestPlayerInstallerURL();
			await context.Response.WriteAsJsonAsync(url);
		}

		private static string GetLatestPlayerInstallerURL()
		{
			var latestPlayerInstaller = new DirectoryInfo(playerFolder)
									.GetFiles("*.exe")
									.OrderBy(x => x.LastWriteTime)
									.First();
			return GetURLFromAbsolutePath(latestPlayerInstaller.FullName);
		}

		[Route("GET", "/api/latest_version_editor_url")]
		[Route("GET", "/api/v1/latest_version_editor_url")]
		private static async Task LatestEditorURLGet(HttpContext context)
		{
			var url = GetLatestEditorInstallerURL();
			await context.Response.WriteAsJsonAsync(url);
		}

		private static string GetLatestEditorInstallerURL()
		{
			var latestEditorInstaller = new DirectoryInfo(editorFolder)
						.GetFiles("*.exe")
						.OrderBy(x => x.LastWriteTime)
						.First();
			return GetURLFromAbsolutePath(latestEditorInstaller.FullName);
		}

		private static string GetURLFromAbsolutePath(string absolutePath)
		{
			return absolutePath.Replace(CommonController.wwwroot, "").Replace(@"\", "/");
		}

	}
}