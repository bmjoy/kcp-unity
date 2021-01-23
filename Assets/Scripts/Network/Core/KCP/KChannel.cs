﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Network;
using UnityEngine;

namespace ETModel
{
	public struct WaitSendBuffer
	{
		public byte[] Bytes;
		public int Length;

		public WaitSendBuffer(byte[] bytes, int length)
		{
			this.Bytes = bytes;
			this.Length = length;
		}
	}

#if dynamic_kcp

#else
	public class KChannel : AChannel
	{
		private Socket socket;

		private KCP kcp;

		private readonly Queue<WaitSendBuffer> sendBuffer = new Queue<WaitSendBuffer>();

		private bool isConnected;
		
		private readonly IPEndPoint remoteEndPoint;

		private uint lastRecvTime;
		
		private readonly uint createTime;

		public uint LocalConn { get; private set; }
		public uint RemoteConn { get; private set; }

		private readonly MemoryStream memoryStream;
		
		// connect
		public KChannel(uint localConn, Socket socket, IPEndPoint remoteEndPoint, KService kService) : base(kService, ChannelType.Connect)
		{
			this.memoryStream = this.GetService().MemoryStreamManager.GetStream("message", ushort.MaxValue);

			this.LocalConn = localConn;
			this.socket = socket;
			this.remoteEndPoint = remoteEndPoint;
			this.lastRecvTime = kService.TimeNow;
			this.createTime = kService.TimeNow;
			
			this.HandleConnnect(localConn);
		}


		public void Dispose()
		{
			try
			{
				if (this.Error == ErrorCode.ERR_Success)
				{
					for (int i = 0; i < 4; i++)
					{
						this.Disconnect();
					}
				}
			}
			catch (Exception)
			{
				// ignored
			}

			if (this.kcp != null)
			{
				kcp.Release();
				this.kcp = null;
			}
			this.socket = null;
			this.memoryStream.Dispose();
		}

		public override MemoryStream Stream
		{
			get
			{
				return this.memoryStream;
			}
		}

		public void Disconnect(int error)
		{
			this.OnError(error);
		}

		private KService GetService()
		{
			return (KService)this.Service;
		}

		public void HandleConnnect(uint remoteConn)
		{
			if (this.isConnected)
			{
				return;
			}

			this.RemoteConn = remoteConn;

			this.kcp = new KCP(this.RemoteConn, new IntPtr(this.LocalConn));
			SetOutput();
			kcp.NoDelay(1, 10, 1, 1);
			kcp.WndSize(32, 32);
			kcp.SetMTU(470);

			this.isConnected = true;
			this.lastRecvTime = this.GetService().TimeNow;
			
			Connect();
		}

		public void Accept()
		{
			if (this.socket == null)
			{
				return;
			}
			
			uint timeNow = this.GetService().TimeNow;

			try
			{
				byte[] buffer = this.memoryStream.GetBuffer();
				buffer.WriteTo(0, KcpProtocalType.ACK);
				buffer.WriteTo(1, LocalConn);
				buffer.WriteTo(5, RemoteConn);
				this.socket.SendTo(buffer, 0, 9, SocketFlags.None, remoteEndPoint);
			}
			catch (Exception e)
			{
				Debug.LogError(e);
				this.OnError(ErrorCode.ERR_SocketCantSend);
			}
		}

		/// <summary>
		/// 发送请求连接消息
		/// </summary>
		private void Connect()
		{
			try
			{
				uint timeNow = this.GetService().TimeNow;
				
				this.lastRecvTime = timeNow;
				
				byte[] buffer = this.memoryStream.GetBuffer();
				buffer.WriteTo(0, KcpProtocalType.SYN);
				buffer.WriteTo(1, this.LocalConn);
				this.socket.SendTo(buffer, 0, 5, SocketFlags.None, remoteEndPoint);
			}
			catch (Exception e)
			{
				Debug.LogError(e);
				this.OnError(ErrorCode.ERR_SocketCantSend);
			}
		}

		/// <summary>
		/// 发送请求断开消息
		/// </summary>
		private void Disconnect()
		{
			if (this.socket == null)
			{
				return;
			}
			try
			{
				byte[] buffer = this.memoryStream.GetBuffer();
				buffer.WriteTo(0, KcpProtocalType.FIN);
				buffer.WriteTo(1, this.LocalConn);
				buffer.WriteTo(5, this.RemoteConn);
				buffer.WriteTo(9, (uint)this.Error);
				this.socket.SendTo(buffer, 0, 13, SocketFlags.None, remoteEndPoint);
			}
			catch (Exception e)
			{
				Debug.LogError(e);
				this.OnError(ErrorCode.ERR_SocketCantSend);
			}
		}

		public void Update()
		{
			uint timeNow = this.GetService().TimeNow;
			
			// 如果还没连接上，发送连接请求
			if (!this.isConnected)
			{
//				// 10秒没连接上则报错
//				if (timeNow - this.createTime > 10 * 1000)
//				{
//					this.OnError(ErrorCode.ERR_KcpCantConnect);
//					return;
//				}
//				
//				if (timeNow - this.lastRecvTime < 500)
//				{
//					return;
//				}
			}

			try
			{
				kcp.Update(timeNow);
			}
			catch (Exception e)
			{
				Debug.LogError(e);
				this.OnError(ErrorCode.ERR_SocketError);
				return;
			}


			if (this.kcp != null)
			{
				uint nextUpdateTime = kcp.Check(timeNow);
			}
		}

		private void HandleSend()
		{
			while (true)
			{
				if (this.sendBuffer.Count <= 0)
				{
					break;
				}

				WaitSendBuffer buffer = this.sendBuffer.Dequeue();
				this.KcpSend(buffer.Bytes, buffer.Length);
			}
		}

		public void HandleRecv(byte[] date, int offset, int length)
		{
			this.isConnected = true;
			
			kcp.Input(date, offset, length);

			while (true)
			{
				int n = kcp.PeekSize();
				if (n < 0)
				{
					return;
				}
				if (n == 0)
				{
					this.OnError((int)SocketError.NetworkReset);
					return;
				}

				byte[] buffer = this.memoryStream.GetBuffer();
				this.memoryStream.SetLength(n);
				this.memoryStream.Seek(0, SeekOrigin.Begin);
				int count = kcp.Recv(buffer,0, ushort.MaxValue);
				if (n != count)
				{
					return;
				}
				if (count <= 0)
				{
					return;
				}

				this.lastRecvTime = this.GetService().TimeNow;

				this.OnRead(this.memoryStream);
			}
		}

		public override void Start()
		{
		}

		public void Output(byte[] bytes, int count)
		{
			try
			{
				if (count == 0)
				{
					Debug.LogError($"output 0");
					return;
				}

				byte[] buffer = this.memoryStream.GetBuffer();
				
				Buffer.BlockCopy(bytes, 0,buffer, 0, count);
				this.socket.SendTo(buffer, 0, count, SocketFlags.None, this.remoteEndPoint);
			}
			catch (Exception e)
			{
				Debug.LogError(e);
				this.OnError(ErrorCode.ERR_SocketCantSend);
			}
		}
		
		private KCP.OutputDelegate kcpOutput;
		public void SetOutput()
		{
			kcpOutput = KcpOutput;
			kcp.SetOutput(kcpOutput);
		}


		public static void KcpOutput(byte[] bytes, int len, object user)
        {
            KService.Output(bytes, len, user);
        }

        private void KcpSend(byte[] buffers, int length)
		{
			kcp.Send(buffers,0, length);
		}
		
		private void Send(byte[] buffer, int index, int length)
		{
			if (isConnected)
			{
				this.KcpSend(buffer, length);
				return;
			}

			this.sendBuffer.Enqueue(new WaitSendBuffer(buffer, length));
		}

		public override void Send(MemoryStream stream)
		{
			if (this.kcp != null)
			{
				// 检查等待发送的消息，如果超出两倍窗口大小，应该断开连接
				if (kcp.WaitSnd() > 256 * 2)
				{
					this.OnError(ErrorCode.ERR_KcpWaitSendSizeTooLarge);
					return;
				}
			}

			ushort size = (ushort)(stream.Length - stream.Position);
			byte[] bytes = stream.GetBuffer();
			Send(bytes, 0, size);
		}
	}

#endif
	
}
