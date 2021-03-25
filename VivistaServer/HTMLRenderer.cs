using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Fluid;

namespace VivistaServer
{
	public class HTMLRenderer
	{
		private static FluidParser parser = new FluidParser();
		private static Dictionary<string, IFluidTemplate> templateCache;

		public HTMLRenderer()
		{
			var files = Directory.EnumerateFiles("Templates", "*", SearchOption.AllDirectories);

			foreach (var file in files)
			{
				var rawTemplate = File.ReadAllText(file);
				var template = parser.Parse(rawTemplate);
				templateCache[file] = template;
			}
		}

		//TODO(Simon): Consider also caching last file write time, so we can auto update pages
		public static async Task<string> Render(string templateName, TemplateContext context)
		{
			if (templateCache.TryGetValue(templateName, out var template))
			{
				return await template.RenderAsync(context);
			}

			throw new Exception($"Something went wrong while rendering {templateName}");
		}
	}
}