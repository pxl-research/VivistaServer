using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Fluid;

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
		private static FluidParser parser = new FluidParser();
		private static Dictionary<string, IFluidTemplate> templateCache = new Dictionary<string, IFluidTemplate>();
		private static Dictionary<BaseLayout, IFluidTemplate> layoutCache = new Dictionary<BaseLayout, IFluidTemplate>();

		static HTMLRenderer()
		{
			var files = Directory.EnumerateFiles("Templates", "*.liquid", SearchOption.AllDirectories);

			foreach (var file in files)
			{
				var rawTemplate = File.ReadAllText(file);
				var template = parser.Parse(rawTemplate);
				templateCache[file] = template;
			}
		}

		public static void RegisterLayout(BaseLayout layout, string file)
		{
			var rawTemplate = File.ReadAllText(file);
			var template = parser.Parse(rawTemplate);
			layoutCache[layout] = template;
		}

		//TODO(Simon): Consider also caching last file write time, so we can auto update pages
		public static async Task<string> Render(string templateName, TemplateContext context, BaseLayout layout = BaseLayout.Web)
		{
			if (templateCache.TryGetValue(templateName, out var template))
			{
				string result;
				if (context == null)
				{
					result = await template.RenderAsync();
				}
				else
				{
					context.MemberAccessStrategy = new UnsafeMemberAccessStrategy();
					context.MemberAccessStrategy.Register(context.Model.GetType());
					result = await template.RenderAsync(context);
				}

				if (layout != BaseLayout.None)
				{
					var content = new TemplateContext(new { content = result });
					result = await layoutCache[layout].RenderAsync(content);
				}

				return result;
			}
			else
			{
				throw new Exception($"Something went wrong while rendering {templateName}");
			}
		}
	}
}