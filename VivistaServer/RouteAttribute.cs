using System;

namespace VivistaServer
{
	[AttributeUsage(AttributeTargets.Method)]
	public class RouteAttribute : Attribute
	{
		public string method;
		public string route;

		public RouteAttribute(string method, string route)
		{
			this.method = method;
			this.route = route;
		}
	}
}