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

		private static string GetLatestPlayerInstallerURL()
		{
			var latestPlayerInstaller = new DirectoryInfo(playerFolder)
									.GetFiles("*.exe")
									.OrderBy(x => x.LastWriteTime)
									.First();
			return GetURLFromAbsolutePath(latestPlayerInstaller.FullName);
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