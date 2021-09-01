using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Fluid;
using Fluid.Values;
using Microsoft.AspNetCore.Http;

namespace VivistaServer
{
	public enum BaseLayout
	{
		None,
		Web,
		Mail
	}

	public class HTMLRenderer
	{
		private static string templateDirectory = "Templates";

		private static FluidParser parser = new FluidParser();
		private static Dictionary<string, IFluidTemplate> templateCache = new Dictionary<string, IFluidTemplate>();
		private static Dictionary<BaseLayout, IFluidTemplate> layoutCache = new Dictionary<BaseLayout, IFluidTemplate>();

		private static UnsafeMemberAccessStrategy defaultAccessStrategy = new UnsafeMemberAccessStrategy();

		private static FileSystemWatcher watcher = new FileSystemWatcher(templateDirectory, "*.liquid");

		static HTMLRenderer()
		{
			var files = Directory.EnumerateFiles(templateDirectory, "*.liquid", SearchOption.AllDirectories);

			foreach (var file in files)
			{
				var rawTemplate = File.ReadAllText(file);
				var template = parser.Parse(rawTemplate);
				templateCache[file] = template;
			}

			InitWatcher();
		}

		public static void RegisterLayout(BaseLayout layout, string file)
		{
			var rawTemplate = File.ReadAllText(file);
			var template = parser.Parse(rawTemplate);
			layoutCache[layout] = template;
		}

		public static ValueTask<FluidValue> GuidBase64(FluidValue input, FilterArguments arguments, TemplateContext context)
		{
			if (input.Type != FluidValues.Object)
			{
				throw new ArgumentException("Value should be of Guid Type");
			}

			var objValue = input.ToObjectValue();
			if (objValue is Guid guid)
			{
				return new StringValue(guid.Encode());
			}

			throw new ArgumentException("Value should be of Guid Type");
		}

		public static async Task<string> Render(HttpContext httpContext, string templateName, TemplateContext context, BaseLayout layout = BaseLayout.Web)
		{
			var watch = Stopwatch.StartNew();
			if (templateCache.TryGetValue(templateName, out var template))
			{
				var user = await UserSessions.GetLoggedInUser(httpContext);
				string result;

				if (context == null)
				{
					context = new TemplateContext();
					context.Options.MemberAccessStrategy = defaultAccessStrategy;
					context.Options.Scope.SetValue("User", FluidValue.Create(user, context.Options));
					context.Options.Filters.AddFilter("guidbase64", GuidBase64);

					result = await template.RenderAsync();
				}
				else
				{
					//NOTE(Simon): This lib requires users to register all used models by their type name.
					//NOTE(cont.): This block prevents having to do it manually.
					context.Options.MemberAccessStrategy = defaultAccessStrategy;
					context.Options.Scope.SetValue("User", FluidValue.Create(user, context.Options));
					context.Options.Filters.AddFilter("guidbase64", GuidBase64);

					result = await template.RenderAsync(context);
				}

				if (layout != BaseLayout.None)
				{
					var content = new TemplateContext(new { content = result });
					context.Options.MemberAccessStrategy = defaultAccessStrategy;
					content.Options.Scope.SetValue("User", FluidValue.Create(user, context.Options));
					context.Options.Filters.AddFilter("guidbase64", GuidBase64);

					result = await layoutCache[layout].RenderAsync(content);
				}

				watch.Stop();
				Console.WriteLine($"rendering: {watch.Elapsed.TotalMilliseconds} ms");

				return result;
			}
			else
			{
				throw new Exception($"Something went wrong while rendering {templateName}");
			}
		}

		private static void InitWatcher()
		{
#if DEBUG
			watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime;

			watcher.Changed += OnChange;
			watcher.Created += OnCreate;
			watcher.Renamed += OnRename;
			watcher.Error += OnError;

			watcher.EnableRaisingEvents = true;
#endif
		}

		private static void OnError(object sender, ErrorEventArgs e)
		{
			Console.WriteLine(e);
		}

#if DEBUG
		private static void OnRename(object sender, RenamedEventArgs e)
		{
			Console.WriteLine($"File Renamed: {e.OldFullPath} => {e.FullPath}");
			templateCache.Remove(e.OldFullPath);
			try
			{
				templateCache.TryAdd(e.FullPath, parser.Parse(File.ReadAllText(e.FullPath)));
			}
			catch { Debug.WriteLine("Error updating template: " + e);}
		}

		private static void OnCreate(object sender, FileSystemEventArgs e)
		{
			Console.WriteLine($"File Created: {e.FullPath}");
			try
			{
				templateCache.TryAdd(e.FullPath, parser.Parse(File.ReadAllText(e.FullPath)));
			}
			catch { Debug.WriteLine("Error updating template: " + e);}
		}

		private static void OnChange(object sender, FileSystemEventArgs e)
		{
			Console.WriteLine($"File Change: {e.FullPath}");
			try
			{
				string raw = File.ReadAllText(e.FullPath);
				templateCache[e.FullPath] = parser.Parse(raw);
			}
			catch { Debug.WriteLine("Error updating template: " + e);}
		}
#endif
	}
}