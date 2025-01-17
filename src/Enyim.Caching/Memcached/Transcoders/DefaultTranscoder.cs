using System;
using System.Linq;
using System.IO;
using System.Text;
using System.Runtime.Serialization;
using Newtonsoft.Json.Bson;
using System.Collections;
using System.Reflection;
using Newtonsoft.Json;

namespace Enyim.Caching.Memcached
{
    /// <summary>
    /// Default <see cref="T:Enyim.Caching.Memcached.ITranscoder"/> implementation. Primitive types are manually serialized, the rest is serialized using <see cref="T:System.Runtime.Serialization.Formatters.Binary.BinaryFormatter"/>.
    /// </summary>
    public class DefaultTranscoder : ITranscoder
    {
        public const uint RawDataFlag = 0xfa52;
        private static readonly ArraySegment<byte> NullArray = new ArraySegment<byte>(new byte[0]);

        CacheItem ITranscoder.Serialize(object value)
        {
            return Serialize(value);
        }

        object ITranscoder.Deserialize(CacheItem item)
        {
            return Deserialize(item);
        }

        public virtual T Deserialize<T>(CacheItem item)
        {
            if (typeof(T).GetTypeCode() != TypeCode.Object || typeof(T) == typeof(Byte[]))
            {
                var value = Deserialize(item);
                if (value != null)
                {
                    if (typeof(T) == typeof(Guid))
                    {
                        return (T)(object)new Guid((string)value);
                    }
                    else
                    {
                        return (T)value;
                    }
                }
                else
                {
                    return default(T);
                }
            }

            using (var ms = new MemoryStream(item.Data.ToArray()))
            {
                using (var reader = new BsonDataReader(ms))
                {
                    if (typeof(T).GetTypeInfo().ImplementedInterfaces.Contains(typeof(IEnumerable)))
                    {
                        reader.ReadRootValueAsArray = true;
                    }
                    var serializer = new JsonSerializer();
                    return serializer.Deserialize<T>(reader);
                }
            }
        }

        protected virtual CacheItem Serialize(object value)
        {
            // raw data is a special case when some1 passes in a buffer (byte[] or ArraySegment<byte>)
            if (value is ArraySegment<byte>)
            {
                // ArraySegment<byte> is only passed in when a part of buffer is being 
                // serialized, usually from a MemoryStream (To avoid duplicating arrays 
                // the byte[] returned by MemoryStream.GetBuffer is placed into an ArraySegment.)
                return new CacheItem(RawDataFlag, (ArraySegment<byte>)value);
            }

            var tmpByteArray = value as byte[];

            // - or we just received a byte[]. No further processing is needed.
            if (tmpByteArray != null)
            {
                return new CacheItem(RawDataFlag, new ArraySegment<byte>(tmpByteArray));
            }

            ArraySegment<byte> data;
            // TypeCode.DBNull is 2
            TypeCode code = value == null ? (TypeCode)2 : Type.GetTypeCode(value.GetType());

            switch (code)
            {
                case (TypeCode)2: data = SerializeNull(); break; // TypeCode.DBNull
                case TypeCode.String: data = SerializeString(value.ToString()); break;
                case TypeCode.Boolean: data = SerializeBoolean((bool)value); break;
                case TypeCode.SByte: data = SerializeSByte((sbyte)value); break;
                case TypeCode.Byte: data = SerializeByte((byte)value); break;
                case TypeCode.Int16: data = SerializeInt16((short)value); break;
                case TypeCode.Int32: data = SerializeInt32((int)value); break;
                case TypeCode.Int64: data = SerializeInt64((long)value); break;
                case TypeCode.UInt16: data = SerializeUInt16((ushort)value); break;
                case TypeCode.UInt32: data = SerializeUInt32((uint)value); break;
                case TypeCode.UInt64: data = SerializeUInt64((ulong)value); break;
                case TypeCode.Char: data = SerializeChar((char)value); break;
                case TypeCode.DateTime: data = SerializeDateTime((DateTime)value); break;
                case TypeCode.Double: data = SerializeDouble((double)value); break;
                case TypeCode.Single: data = SerializeSingle((float)value); break;
                default:
                    code = TypeCode.Object;
                    data = SerializeObject(value);
                    break;
            }

            return new CacheItem(TypeCodeToFlag(code), data);
        }

        public static uint TypeCodeToFlag(TypeCode code)
        {
            return (uint)((int)code | 0x0100);
        }

        public static bool IsFlagHandled(uint flag)
        {
            return (flag & 0x100) == 0x100;
        }

        protected virtual object Deserialize(CacheItem item)
        {
            if (item.Data.Array == null)
                return null;

            if (item.Flags == RawDataFlag)
            {
                var tmp = item.Data;

                if (tmp.Count == tmp.Array.Length)
                    return tmp.Array;

                // we should never arrive here, but it's better to be safe than sorry
                var retval = new byte[tmp.Count];

                Array.Copy(tmp.Array, tmp.Offset, retval, 0, tmp.Count);

                return retval;
            }

            var code = (TypeCode)(item.Flags & 0xff);

            var data = item.Data;

            switch (code)
            {
                // incrementing a non-existing key then getting it
                // returns as a string, but the flag will be 0
                // so treat all 0 flagged items as string
                // this may help inter-client data management as well
                //
                // however we store 'null' as Empty + an empty array, 
                // so this must special-cased for compatibilty with 
                // earlier versions. we introduced DBNull as null marker in emc2.6
                case TypeCode.Empty:
                    return (data.Array == null || data.Count == 0)
                            ? null
                            : DeserializeString(data);

                case (TypeCode)2: return null; // TypeCode.DBNull
                case TypeCode.String: return DeserializeString(data);
                case TypeCode.Boolean: return DeserializeBoolean(data);
                case TypeCode.Int16: return DeserializeInt16(data);
                case TypeCode.Int32: return DeserializeInt32(data);
                case TypeCode.Int64: return DeserializeInt64(data);
                case TypeCode.UInt16: return DeserializeUInt16(data);
                case TypeCode.UInt32: return DeserializeUInt32(data);
                case TypeCode.UInt64: return DeserializeUInt64(data);
                case TypeCode.Char: return DeserializeChar(data);
                case TypeCode.DateTime: return DeserializeDateTime(data);
                case TypeCode.Double: return DeserializeDouble(data);
                case TypeCode.Single: return DeserializeSingle(data);
                case TypeCode.Byte: return DeserializeByte(data);
                case TypeCode.SByte: return DeserializeSByte(data);

                // backward compatibility
                // earlier versions serialized decimals with TypeCode.Decimal
                // even though they were saved by BinaryFormatter
                case TypeCode.Decimal:
                case TypeCode.Object: return DeserializeObject(data);
                default: throw new InvalidOperationException("Unknown TypeCode was returned: " + code);
            }
        }

        #region [ Typed serialization          ]

        protected virtual ArraySegment<byte> SerializeNull()
        {
            return NullArray;
        }

        protected virtual ArraySegment<byte> SerializeString(string value)
        {
            return new ArraySegment<byte>(Encoding.UTF8.GetBytes((string)value));
        }

        protected virtual ArraySegment<byte> SerializeByte(byte value)
        {
            return new ArraySegment<byte>(new byte[] { value });
        }

        protected virtual ArraySegment<byte> SerializeSByte(sbyte value)
        {
            return new ArraySegment<byte>(new byte[] { (byte)value });
        }

        protected virtual ArraySegment<byte> SerializeBoolean(bool value)
        {
            return new ArraySegment<byte>(BitConverter.GetBytes(value));
        }

        protected virtual ArraySegment<byte> SerializeInt16(Int16 value)
        {
            return new ArraySegment<byte>(BitConverter.GetBytes(value));
        }

        protected virtual ArraySegment<byte> SerializeInt32(Int32 value)
        {
            return new ArraySegment<byte>(BitConverter.GetBytes(value));
        }

        protected virtual ArraySegment<byte> SerializeInt64(Int64 value)
        {
            return new ArraySegment<byte>(BitConverter.GetBytes(value));
        }

        protected virtual ArraySegment<byte> SerializeUInt16(UInt16 value)
        {
            return new ArraySegment<byte>(BitConverter.GetBytes(value));
        }

        protected virtual ArraySegment<byte> SerializeUInt32(UInt32 value)
        {
            return new ArraySegment<byte>(BitConverter.GetBytes(value));
        }

        protected virtual ArraySegment<byte> SerializeUInt64(UInt64 value)
        {
            return new ArraySegment<byte>(BitConverter.GetBytes(value));
        }

        protected virtual ArraySegment<byte> SerializeChar(char value)
        {
            return new ArraySegment<byte>(BitConverter.GetBytes(value));
        }

        protected virtual ArraySegment<byte> SerializeDateTime(DateTime value)
        {
            return new ArraySegment<byte>(BitConverter.GetBytes(value.ToBinary()));
        }

        protected virtual ArraySegment<byte> SerializeDouble(Double value)
        {
            return new ArraySegment<byte>(BitConverter.GetBytes(value));
        }

        protected virtual ArraySegment<byte> SerializeSingle(Single value)
        {
            return new ArraySegment<byte>(BitConverter.GetBytes(value));
        }

        protected virtual ArraySegment<byte> SerializeObject(object value)
        {
            using (var ms = new MemoryStream())
            {
                using (var writer = new BsonDataWriter(ms))
                {
                    var serializer = new JsonSerializer();
                    serializer.Serialize(writer, value);
                    return new ArraySegment<byte>(ms.ToArray(), 0, (int)ms.Length);
                }
            }
        }

        #endregion
        #region [ Typed deserialization        ]

        protected virtual string DeserializeString(ArraySegment<byte> value)
        {
            return Encoding.UTF8.GetString(value.Array, value.Offset, value.Count);
        }

        protected virtual bool DeserializeBoolean(ArraySegment<byte> value)
        {
            return BitConverter.ToBoolean(value.Array, value.Offset);
        }

        protected virtual short DeserializeInt16(ArraySegment<byte> value)
        {
            return BitConverter.ToInt16(value.Array, value.Offset);
        }

        protected virtual int DeserializeInt32(ArraySegment<byte> value)
        {
            return BitConverter.ToInt32(value.Array, value.Offset);
        }

        protected virtual long DeserializeInt64(ArraySegment<byte> value)
        {
            return BitConverter.ToInt64(value.Array, value.Offset);
        }

        protected virtual ushort DeserializeUInt16(ArraySegment<byte> value)
        {
            return BitConverter.ToUInt16(value.Array, value.Offset);
        }

        protected virtual uint DeserializeUInt32(ArraySegment<byte> value)
        {
            return BitConverter.ToUInt32(value.Array, value.Offset);
        }

        protected virtual ulong DeserializeUInt64(ArraySegment<byte> value)
        {
            return BitConverter.ToUInt64(value.Array, value.Offset);
        }

        protected virtual char DeserializeChar(ArraySegment<byte> value)
        {
            return BitConverter.ToChar(value.Array, value.Offset);
        }

        protected virtual DateTime DeserializeDateTime(ArraySegment<byte> value)
        {
            return DateTime.FromBinary(BitConverter.ToInt64(value.Array, value.Offset));
        }

        protected virtual double DeserializeDouble(ArraySegment<byte> value)
        {
            return BitConverter.ToDouble(value.Array, value.Offset);
        }

        protected virtual float DeserializeSingle(ArraySegment<byte> value)
        {
            return BitConverter.ToSingle(value.Array, value.Offset);
        }

        protected virtual byte DeserializeByte(ArraySegment<byte> data)
        {
            return data.Array[data.Offset];
        }

        protected virtual sbyte DeserializeSByte(ArraySegment<byte> data)
        {
            return (sbyte)data.Array[data.Offset];
        }

        protected virtual object DeserializeObject(ArraySegment<byte> value)
        {
            using (var ms = new MemoryStream(value.Array, value.Offset, value.Count))
            {
                using (var reader = new BsonDataReader(ms))
                {
                    JsonSerializer serializer = new JsonSerializer();
                    return serializer.Deserialize(reader);
                }
            }
        }

        #endregion
    }
}

#region [ License information          ]
/* ************************************************************
 * 
 *    Copyright (c) 2010 Attila Kisk? enyim.com
 *    
 *    Licensed under the Apache License, Version 2.0 (the "License");
 *    you may not use this file except in compliance with the License.
 *    You may obtain a copy of the License at
 *    
 *        http://www.apache.org/licenses/LICENSE-2.0
 *    
 *    Unless required by applicable law or agreed to in writing, software
 *    distributed under the License is distributed on an "AS IS" BASIS,
 *    WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 *    See the License for the specific language governing permissions and
 *    limitations under the License.
 *    
 * ************************************************************/
#endregion
