using System;
using System.Globalization;

namespace Enyim.Caching.Memcached.Operations.Text
{
	internal static class GetHelper
	{
		private static log4net.ILog log = log4net.LogManager.GetLogger(typeof(GetHelper));

		public static void FinishCurrent(PooledSocket socket)
		{
			string response = TextSocketHelper.ReadResponse(socket);

			if (String.Compare(response, "END", StringComparison.Ordinal) != 0)
				throw new MemcachedClientException("No END was received.");
		}

		public static GetResponse ReadItem(PooledSocket socket)
		{
			string description = TextSocketHelper.ReadResponse(socket);

			if (String.Compare(description, "END", StringComparison.Ordinal) == 0)
				return null;

			if (description.Length < 6 || String.Compare(description, 0, "VALUE ", 0, 6, StringComparison.Ordinal) != 0)
				throw new MemcachedClientException("No VALUE response received.\r\n" + description);

			ulong cas = 0;
			string[] parts = description.Split(' ');

			// response is:
			// VALUE <key> <flags> <bytes> [<cas unique>]
			// 0     1     2       3       4
			//
			// cas only exists in 1.2.4+
			//
			if (parts.Length == 5)
			{
				if (!UInt64.TryParse(parts[4], out cas))
					throw new MemcachedClientException("Invalid CAS VALUE received.");

			}
			else if (parts.Length < 4)
			{
				throw new MemcachedClientException("Invalid VALUE response received: " + description);
			}

			ushort flags = UInt16.Parse(parts[2], CultureInfo.InvariantCulture);
			int length = Int32.Parse(parts[3], CultureInfo.InvariantCulture);

			byte[] allData = new byte[length];
			byte[] eod = new byte[2];

			socket.Read(allData, 0, length);
			socket.Read(eod, 0, 2); // data is terminated by \r\n

			GetResponse retval = new GetResponse(parts[1], flags, cas, allData);

			if (log.IsDebugEnabled)
				log.DebugFormat("Received value. Data type: {0}, size: {1}.", retval.Item.Flags, retval.Item.Data.Count);

			return retval;
		}
	}

	#region [ T:GetResponse                  ]
	internal class GetResponse
	{
		private GetResponse() { }
		public GetResponse(string key, ushort flags, ulong casValue, byte[] data) : this(key, flags, casValue, data, 0, data.Length) { }

		public GetResponse(string key, ushort flags, ulong casValue, byte[] data, int offset, int count)
		{
			this.Key = key;
			this.CasValue = casValue;
			
			this.Item = new CacheItem(flags, new ArraySegment<byte>(data, offset, count));
		}

		public readonly string Key;
		public readonly ulong CasValue;
		public readonly CacheItem Item;
	}
	#endregion

}

#region [ License information          ]
/* ************************************************************
 *
 * Copyright (c) Attila Kisk�, enyim.com
 *
 * This source code is subject to terms and conditions of 
 * Microsoft Permissive License (Ms-PL).
 * 
 * A copy of the license can be found in the License.html
 * file at the root of this distribution. If you can not 
 * locate the License, please send an email to a@enyim.com
 * 
 * By using this source code in any fashion, you are 
 * agreeing to be bound by the terms of the Microsoft 
 * Permissive License.
 *
 * You must not remove this notice, or any other, from this
 * software.
 *
 * ************************************************************/
#endregion