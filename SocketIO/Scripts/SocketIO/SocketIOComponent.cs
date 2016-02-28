#region License
/*
 * SocketIO.cs
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
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;
using WebSocketSharp;

namespace SocketIO
{
	public class SocketIoComponent : MonoBehaviour
	{
		#region Public Properties

		public string Url = "ws://127.0.0.1:4567/Socket.io/?EIO=3&transport=websocket";
		public bool AutoConnect;
		public int ReconnectDelay = 5;
		public float AckExpirationTime = 30f;
		public float PingInterval = 25f;
		public float PingTimeout = 60f;

		public WebSocket Socket { get; private set; }
	    public string Sid { get; set; }
		public bool IsConnected { get { return _connected; } }

		#endregion

		#region Private Properties

		private volatile bool _connected;
		private volatile bool _thPinging;
		private volatile bool _thPong;
		private volatile bool _wsConnected;

	    public delegate void SocketIoEventCallback(SocketMessage message);

		private Dictionary<string, SocketIoEventCallback> _handlers;
		private List<Ack> _ackList;

		private int _packetId;

		private object _eventQueueLock;
		private Queue<SocketMessage> _eventQueue;

		private object _ackQueueLock;
		private Queue<Packet> _ackQueue;

        private Thread _socketThread;
        private Thread _pingThread;

        #endregion

#if SOCKET_IO_DEBUG
		public Action<string> debugMethod;
#endif

        #region Unity interface

        public void Awake()
		{
			_handlers = new Dictionary<string, SocketIoEventCallback>();
			_ackList = new List<Ack>();
			Sid = null;
			_packetId = 0;
            _connected = false;

            InitializeWebSocket();
            InitializeThreading();

#if SOCKET_IO_DEBUG
			if(debugMethod == null) { debugMethod = Debug.Log; };
#endif
		}

	    private void InitializeWebSocket()
	    {
	        Socket = new WebSocket(Url);
            Socket.OnOpen += OnOpen;
            Socket.OnMessage += OnMessage;
            Socket.OnError += OnError;
            Socket.OnClose += OnClose;
            _wsConnected = false;
        }

	    private void InitializeThreading()
	    {
            _eventQueueLock = new object();
            _ackQueueLock = new object();
            _eventQueue = new Queue<SocketMessage>();
            _ackQueue = new Queue<Packet>();
        }

		public void Start()
		{
			if (AutoConnect)
                Connect();
		}

		public void Update()
		{
			lock(_eventQueueLock){ 
				while(_eventQueue.Count > 0){
					EmitEvent(_eventQueue.Dequeue());
				}
			}

			lock(_ackQueueLock){
				while(_ackQueue.Count > 0){
					InvokeAck(_ackQueue.Dequeue());
				}
			}

			if (_wsConnected != Socket.IsConnected)
			{
			    _wsConnected = Socket.IsConnected;
			    EmitEvent(_wsConnected ? "connect" : "disconnect");
			}

            // GC expired acks
            if (_ackList.Count == 0 || DateTime.Now.Subtract(_ackList[0].time).TotalSeconds < AckExpirationTime)
                return;
			_ackList.RemoveAt(0);
		}

		public void OnDestroy()
		{
			if (_socketThread != null)
                _socketThread.Abort();
			if (_pingThread != null)
                _pingThread.Abort();
		}

		public void OnApplicationQuit()
		{
			Close();
		}

		#endregion

		#region Public Interface
		
		public void Connect()
		{
			_connected = true;
            _socketThread = new Thread(RunSocketThread);
            _pingThread = new Thread(RunPingThread);
            _socketThread.Start(Socket);
            _pingThread.Start(Socket);
		}

		public void Close()
		{
			EmitClose();
			_connected = false;
		}

		public void On(string ev, SocketIoEventCallback eventCallback)
		{
		    if (!_handlers.ContainsKey(ev))
		    {
		        _handlers[ev] = eventCallback;
		    }
		    else
		    {
                _handlers[ev] += eventCallback;
            }
		}

		public void Off(string ev, SocketIoEventCallback eventCallback)
		{
			if (!_handlers.ContainsKey(ev)) {
				#if SOCKET_IO_DEBUG
				debugMethod.Invoke("[SocketIO] No callbacks registered for event: " + ev);
				#endif
				return;
			}

		    _handlers[ev] -= eventCallback;
		}

		public void Emit(string ev)
		{
			EmitMessage(-1, string.Format("[\"{0}\"]", ev));
		}

		public void Emit(string ev, Action<JSONObject> action)
		{
			EmitMessage(++_packetId, string.Format("[\"{0}\"]", ev));
			_ackList.Add(new Ack(_packetId, action));
		}

		public void Emit(string ev, JSONObject data)
		{
			EmitMessage(-1, string.Format("[\"{0}\",{1}]", ev, data));
		}

		public void Emit(string ev, JSONObject data, Action<JSONObject> action)
		{
			EmitMessage(++_packetId, string.Format("[\"{0}\",{1}]", ev, data));
			_ackList.Add(new Ack(_packetId, action));
		}

		#endregion

		#region Private Methods

		private void RunSocketThread(object obj)
		{
			WebSocket webSocket = (WebSocket)obj;
			while (_connected)
            {
				if (webSocket.IsConnected)
                {
					Thread.Sleep(ReconnectDelay);
				}
                else
                {
					webSocket.Connect();
				}
			}
			webSocket.Close();
		}

		private void RunPingThread(object obj)
		{
			WebSocket webSocket = (WebSocket)obj;

			int timeoutMilis = Mathf.FloorToInt(PingTimeout * 1000);
			int intervalMilis = Mathf.FloorToInt(PingInterval * 1000);

			DateTime pingStart;

			while(_connected)
			{
				if(!_wsConnected){
					Thread.Sleep(ReconnectDelay);
				} else {
					_thPinging = true;
					_thPong =  false;
					
					EmitPacket(new Packet(Packet.Engine.PING));
					pingStart = DateTime.Now;
					
					while(webSocket.IsConnected && _thPinging && (DateTime.Now.Subtract(pingStart).TotalSeconds < timeoutMilis)){
						Thread.Sleep(200);
					}
					
					if(!_thPong){
						webSocket.Close();
					}

					Thread.Sleep(intervalMilis);
				}
			}
		}

		private void EmitMessage(int id, string raw)
		{
			EmitPacket(new Packet(Packet.Engine.MESSAGE, Packet.Socket.EVENT, 0, "/", id, new JSONObject(raw)));
		}

		private void EmitClose()
		{
			EmitPacket(new Packet(Packet.Engine.MESSAGE, Packet.Socket.DISCONNECT, 0, "/", -1, new JSONObject("")));
			EmitPacket(new Packet(Packet.Engine.CLOSE));
		}

		private void EmitPacket(Packet packet)
		{
			#if SOCKET_IO_DEBUG
			debugMethod.Invoke("[SocketIO] " + packet);
			#endif
			
			try {
				Socket.Send(packet.Encode());
			} catch(SocketIOException ex) {
				#if SOCKET_IO_DEBUG
				debugMethod.Invoke(ex.ToString());
				#endif
			}
		}

		private void OnOpen(object sender, EventArgs e)
		{
			EmitEvent("open");
		}

	    private void OnMessage(object sender, MessageEventArgs e)
	    {
#if SOCKET_IO_DEBUG
			debugMethod.Invoke("[SocketIO] Raw message: " + e.Data);
#endif
	        var packet = Packet.Decode(e);
	        switch (packet.engine)
	        {
	            case Packet.Engine.OPEN:
	                HandleOpen(packet);
	                break;
	            case Packet.Engine.CLOSE:
	                EmitEvent("close");
	                break;
	            case Packet.Engine.PING:
	                HandlePing();
	                break;
	            case Packet.Engine.PONG:
	                HandlePong();
	                break;
	            case Packet.Engine.MESSAGE:
	                HandleMessage(packet);
	                break;
	        }
	    }

	    private void HandleOpen(Packet packet)
		{
			#if SOCKET_IO_DEBUG
			debugMethod.Invoke("[SocketIO] Socket.IO sid: " + packet.json["sid"].str);
			#endif
			Sid = packet.json["sid"].str;
			EmitEvent("open");
		}

		private void HandlePing()
		{
			EmitPacket(new Packet(Packet.Engine.PONG));
		}

		private void HandlePong()
		{
			_thPong = true;
			_thPinging = false;
		}
		
		private void HandleMessage(Packet packet)
		{
			if(packet.json == null)
                return;

			if(packet.socket == Packet.Socket.ACK)
            {
				if (_ackList.Any(t => t.packetId == packet.id))
				{
				    lock (_ackQueueLock)
				    {
				        _ackQueue.Enqueue(packet);
				    }
				    return;
				}

				#if SOCKET_IO_DEBUG
				debugMethod.Invoke("[SocketIO] Ack received for invalid Action: " + packet.id);
				#endif
			}

			if (packet.socket == Packet.Socket.EVENT)
            {
				var e = SocketMessage.Parse(packet.json);
                lock (_eventQueueLock)
                {
                    _eventQueue.Enqueue(e);
                }
			}
		}

		private void OnError(object sender, ErrorEventArgs e)
		{
			EmitEvent(new SocketMessage("error", JSONObject.CreateStringObject(e.Message)));
		}

		private void OnClose(object sender, CloseEventArgs e)
		{
			EmitEvent("close");
		}

		private void EmitEvent(string type)
		{
			EmitEvent(new SocketMessage(type));
		}

		private void EmitEvent(SocketMessage ev)
		{
			if (!_handlers.ContainsKey(ev.Name))
                return;

		    try
		    {
		        _handlers[ev.Name](ev);
		    }
            catch (Exception ex)
            {
#if SOCKET_IO_DEBUG
				debugMethod.Invoke(ex.ToString());
#endif
            }
		}

		private void InvokeAck(Packet packet)
		{
			for (var i = 0; i < _ackList.Count; i++)
            {
				if(_ackList[i].packetId != packet.id)
                    continue;
				var ack = _ackList[i];
				_ackList.RemoveAt(i);
				ack.Invoke(packet.json);
				return;
			}
		}

		#endregion
	}
}
