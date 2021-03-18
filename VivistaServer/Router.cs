using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Npgsql;

namespace VivistaServer
{
	[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
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

	internal struct Route
	{
		public readonly string method;
		public readonly string route;

		public Route(string method, string route)
		{
			this.method = method;
			this.route = route;
		}

		public override int GetHashCode()
		{
			return HashCode.Combine(method, route);
		}
	}

	public class Router
	{
		private delegate Task RouteDelegate(HttpContext context);
		private readonly Dictionary<Route, RouteDelegate> routes;

		public Router()
		{
#if DEBUG
			Asserts();
#endif
			routes = new Dictionary<Route, RouteDelegate>();

			var methods = Assembly.GetExecutingAssembly().GetTypes().
						SelectMany(t => t.GetMethods(BindingFlags.NonPublic | BindingFlags.Static)).
						Where(m => m.GetCustomAttributes(typeof(RouteAttribute), false).Length > 0).ToList();

			foreach (var method in methods)
			{
				var attribs = (RouteAttribute[])method.GetCustomAttributes(typeof(RouteAttribute), false);

				foreach (var attrib in attribs)
				{
					routes.Add(new Route(attrib.method, attrib.route), (RouteDelegate) Delegate.CreateDelegate(typeof(RouteDelegate), null, method));
				}
			}
		}

		public async Task RouteAsync(HttpRequest request, HttpContext context)
		{
			if (routes.TryGetValue(new Route(request.Method, request.Path), out var del))
			{
				await del.Invoke(context);
			}
			else
			{
				await routes[new Route("", "404")].Invoke(context);
			}
		}

#if DEBUG
		private static void Asserts()
		{
			var methods = Assembly.GetExecutingAssembly().GetTypes().
						   SelectMany(t => t.GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)).
						   Where(m => m.GetCustomAttributes(typeof(RouteAttribute), false).Length > 0).ToList();

			foreach (var method in methods)
			{
				if (!method.IsStatic)
				{
					throw new Exception($"Method {method.DeclaringType.FullName}.{method.Name}() should be static if it is to have a {nameof(RouteAttribute)}");
				}

				if (!method.IsPrivate)
				{
					throw new Exception($"Method {method.DeclaringType.FullName}.{method.Name}() should be private if it is to have a {nameof(RouteAttribute)}");
				}

				var parameters = method.GetParameters();
				if (parameters.Length != 1)
				{
					throw new Exception($"Method {method.DeclaringType.FullName}.{method.Name}() should have exactly 1 parameter if it is to have a {nameof(RouteAttribute)}");
				}

				if (parameters[0].ParameterType.Name != nameof(HttpContext))
				{
					throw new Exception($"Method {method.DeclaringType.FullName}.{method.Name}() should have a first parameter of type {nameof(HttpContext)} if it is to have a {nameof(RouteAttribute)}");
				}

				if (method.ReturnType.Name != nameof(Task))
				{
					throw new Exception($"Method {method.DeclaringType.FullName}.{method.Name}() should have a return type of {nameof(Task)} if it is to have a {nameof(RouteAttribute)}");
				}
			}
		}
#endif
	}
}