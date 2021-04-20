using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace VivistaServer
{
	public static class StringHelpers
	{
		public static String NormalizeEmail(this string email)
		{
			return email.ToLowerInvariant().Trim();
		}
	}
}
