using System;
using System.Text;
using System.Globalization;

namespace VivistaServer
{
	public static class StringHelpers
	{
		public static string NormalizeEmail(this string email)
		{
			return email.ToLowerInvariant().Trim();
		}

		public static string NormalizeForSearch(this string str)
		{
			string stFormD = str.Normalize(NormalizationForm.FormD);
			int len = stFormD.Length;
			var sb = new StringBuilder();

			for (int i = 0; i < len; i++)
			{
				var uc = CharUnicodeInfo.GetUnicodeCategory(stFormD[i]);
				if (uc != UnicodeCategory.NonSpacingMark)
				{
					sb.Append(stFormD[i]);
				}
			}
			return sb.ToString().Normalize(NormalizationForm.FormC);
		}
	}

	public static class GuidHelpers
	{
		public static string Encode(this Guid guid)
		{
			string encoded = Convert.ToBase64String(guid.ToByteArray());
			encoded = encoded.Replace("/", "_").Replace("+", "-");
			return encoded.Substring(0, 22);
		}

		public static bool TryDecode(string value, out Guid guid)
		{
			value = value.Replace("_", "/").Replace("-", "+");
			byte[] buffer = Convert.FromBase64String(value + "==");
			try
			{
				guid = new Guid(buffer);
				return true;
			}
			catch
			{
				guid = new Guid();
				return false;
			}
		}
	}
}
