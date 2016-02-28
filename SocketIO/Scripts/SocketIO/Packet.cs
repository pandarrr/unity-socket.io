#region License
/*
 * Packet.cs
 *
 * The MIT License
 *
 * Copyright (c) 2014 Fabio Panettieri
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in
 * all copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
 * THE SOFTWARE.
 */
#endregion
 #define SOCKET_IO_DEBUG
using System;
using System.IO;
using System.Text;
using UnityEngine;
using WebSocketSharp;

namespace SocketIO
{
	public class Packet
	{
        public enum Engine
        {
            UNKNOWN = -1,
            OPEN = 0,
            CLOSE = 1,
            PING = 2,
            PONG = 3,
            MESSAGE = 4,
            UPGRADE = 5,
            NOOP = 6
        }

        public enum Socket
        {
            UNKNOWN = -1,
            CONNECT = 0,
            DISCONNECT = 1,
            EVENT = 2,
            ACK = 3,
            ERROR = 4,
            BINARY_EVENT = 5,
            BINARY_ACK = 6,
            CONTROL = 7
        }

        public Engine engine;
		public Socket socket;

		public int attachments;
		public string nsp;
		public int id;
		public JSONObject json;

		public Packet() 
            : this(Engine.UNKNOWN, Socket.UNKNOWN, -1, "/", -1, null) { }

		public Packet(Engine engine, Socket socket = Socket.UNKNOWN) 
            : this(engine, socket, -1, "/", -1, null) { }

	    public Packet(Engine engine, Socket socket, string nsp, int id, JSONObject json)
	        : this(engine, socket, -1, nsp, id, json) { }

	    public Packet(Engine engine, JSONObject json)
	        : this(engine, Socket.UNKNOWN, -1, "/", -1, json) { }

	    public Packet(Engine engine, Socket socket, int attachments, string nsp, int id, JSONObject json)
		{
			this.engine = engine;
			this.socket = socket;
			this.attachments = attachments;
			this.nsp = nsp;
			this.id = id;
			this.json = json;
		}

        public string Encode()
        {
            try
            {
#if SOCKET_IO_DEBUG
				Debug.Log("[SocketIO] Encoding: " + json);
#endif

                var builder = new StringBuilder();
                
                builder.Append((int) engine);
                if (!engine.Equals(Engine.MESSAGE))
                    return builder.ToString();

                builder.Append((int) socket);

                if (socket == Socket.BINARY_EVENT || socket == Socket.BINARY_ACK)
                {
                    builder.Append(attachments);
                    builder.Append('-');
                }

                if (!string.IsNullOrEmpty(nsp) && !nsp.Equals("/"))
                {
                    builder.Append(nsp);
                    builder.Append(',');
                }

                if (id > -1)
                    builder.Append(id);
            
                if (json != null && !json.ToString().Equals("null"))
                    builder.Append(json.ToString());

#if SOCKET_IO_DEBUG
				Debug.Log("[SocketIO] Encoded: " + builder);
#endif

                return builder.ToString();
            }
            catch (Exception ex)
            {
                throw new SocketIOException("Packet encoding failed: " + this, ex);
            }
        }

        public static Packet Decode(MessageEventArgs args)
        {
            try
            {
#if SOCKET_IO_DEBUG
				Debug.Log("[SocketIO] Decoding: " + args.Data);
#endif
                var data = new StringReader(args.Data);
                Engine engine = (Engine) data.Read();

                if (data.Peek() == '{')
                    return new Packet(engine, new JSONObject(data.ReadToEnd()));

                Socket socket = (Socket) data.Read();
                if (data.Peek() == -1)
                    return new Packet(engine, socket);

                var nsp = ReadNamespace(ref data);
                var id = ReadId(ref data);
                var json = ReadJson(ref data);

                var packet = new Packet(engine, socket, nsp, id, json);
#if SOCKET_IO_DEBUG
                Debug.Log("[SocketIO] Decoded Packet: " + packet);
#endif
                return packet;
            }
            catch (Exception ex)
            {
                throw new SocketIOException("Packet decoding failed: " + args.Data, ex);
            }
        }

	    private static string ReadNamespace(ref StringReader data)
	    {
	        if ('/' != data.Peek())
                return "/";
	        var nsp = "";
	        var next = data.Read();
	        while (next != -1 && next != ',')
	        {
	            nsp += next;
	            next = data.Read();
	        }
	        return nsp;
	    }

	    private static int ReadId(ref StringReader data)
	    {
            var next = (char) data.Peek();
	        if (next == ' ')
                return -1;

            var builder = new StringBuilder();
	        while (char.IsNumber(next))
	        {
	            builder.Append((char) data.Read());
	            next = (char) data.Peek();
	        }
	        return int.Parse(builder.ToString());
	    }

	    private static JSONObject ReadJson(ref StringReader data)
	    {
	        var next = data.Read();
	        if (next == -1)
                return JSONObject.nullJO;

            try
            {
                var json = data.ReadToEnd();
#if SOCKET_IO_DEBUG
                Debug.Log("[SocketIO] Parsing JSON: " + json);
#endif
	            return new JSONObject(json);
	        }
	        catch (Exception ex)
	        {
	            Debug.LogException(ex);
	        }
            return JSONObject.nullJO;
	    }

        public override string ToString()
		{
			return string.Format("[Packet: engine={0}, Socket={1}, attachments={2}, nsp={3}, id={4}, json={5}]", engine, socket, attachments, nsp, id, json);
		}
	}
}
