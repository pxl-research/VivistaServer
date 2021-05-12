﻿using System.IO;
using System.Linq;
using System.Security.Policy;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace VivistaServer
{
	public class InstallController
	{
		private static string playerFolder = CommonController.wwwroot + "\\Installers\\Player";
		private static string editorFolder = CommonController.wwwroot + "\\Installers\\Editor";

		[Route("GET", "/install")]
		private static async Task InstallGet(HttpContext context)
		{
			var playerPath = GetLatestPlayerInstallerURL();
			var editorPath = GetLatestEditorInstallerURL();
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
			var latestPlayerInstaller = new DirectoryInfo(editorFolder)
						.GetFiles("*.exe")
						.OrderBy(x => x.LastWriteTime)
						.First();
			return GetURLFromAbsolutePath(latestPlayerInstaller.FullName);
		}

		private static string GetURLFromAbsolutePath(string absolutePath)
		{
			return absolutePath.Replace(CommonController.wwwroot, "").Replace(@"\", "/");
		}

	}
}