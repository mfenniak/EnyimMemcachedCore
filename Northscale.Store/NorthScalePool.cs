﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Threading;
using Enyim.Caching.Memcached;
using NorthScale.Store.Configuration;
using Enyim.Caching.Configuration;
using Enyim.Caching.Memcached.Protocol.Binary;

namespace NorthScale.Store
{
	/// <summary>
	/// Socket pool using the NorthScale server's dynamic node list
	/// </summary>
	internal class NorthScalePool : IServerPool
	{
		private static readonly log4net.ILog log = log4net.LogManager.GetLogger(typeof(NorthScalePool));

		private INorthScaleClientConfiguration configuration;

		private Uri[] poolUrls;
		private BucketConfigListener configListener;

		private InternalState state;

		private string bucketName;
		private string bucketPassword;

		private object RezSync = new Object();
		private System.Threading.Timer resurrectTimer;
		private bool isTimerActive;
		private long deadTimeoutMsec;

		public NorthScalePool(INorthScaleClientConfiguration configuration) : this(configuration, null) { }

		/// <summary>
		/// Initializes a new instance of the <see cref="T:NorthScale.Store.NorthScalePool" /> class using the specified configuration 
		/// and bucket name. The name also will be used as the bucket password.
		/// </summary>
		/// <param name="configuration">The configuration to be used.</param>
		/// <param name="bucket">The name of the bucket to connect to.</param>
		public NorthScalePool(INorthScaleClientConfiguration configuration, string bucket) : this(configuration, bucket, bucket) { }

		/// <summary>
		/// Initializes a new instance of the <see cref="T:NorthScale.Store.NorthScalePool" /> class using the specified configuration,
		/// bucket name and password.
		/// </summary>
		/// <param name="configuration">The configuration to be used.</param>
		/// <param name="bucket">The name of the bucket to connect to.</param>
		/// <param name="bucketPassword">The password to the bucket.</param>
		/// <remarks> If the password is null, the bucket name will be used. Set to String.Empty to use an empty password.</remarks>
		public NorthScalePool(INorthScaleClientConfiguration configuration, string bucket, string bucketPassword)
		{
			this.configuration = configuration;
			this.bucketName = bucket ?? configuration.Bucket;
			// parameter -> config -> name
			this.bucketPassword = bucketPassword ?? configuration.BucketPassword ?? bucket;

			// make null both if we use the default bucket since we do not need to be authenticated
			if (String.IsNullOrEmpty(this.bucketName) || this.bucketName == "default")
			{
				this.bucketName = null;
				this.bucketPassword = null;
			}

			this.deadTimeoutMsec = (long)this.configuration.SocketPool.DeadTimeout.TotalMilliseconds;
		}

		~NorthScalePool()
		{
			try { ((IDisposable)this).Dispose(); }
			catch { }
		}

		//public VBucketNodeLocator ForwardLocator { get { return this.state.ForwardLocator; } }


		private void InitNodes(ClusterConfig config)
		{
			if (log.IsInfoEnabled) log.Info("Received new configuration.");

			// we cannot overwrite the config while the timer is is running
			lock (this.RezSync)
				this.ReconfigurePool(config);
		}

		private void ReconfigurePool(ClusterConfig config)
		{
			// kill the timer first
			this.isTimerActive = false;
			if (this.resurrectTimer != null)
				this.resurrectTimer.Change(Timeout.Infinite, Timeout.Infinite);

			if (config == null)
			{
				if (log.IsInfoEnabled) log.Info("Config is empty, all nodes are down.");

				Interlocked.Exchange(ref this.state, InternalState.Empty);

				return;
			}

			// these should be disposed after we've been reinitialized
			var oldNodes = this.state == null ? null : this.state.CurrentNodes;

			// default bucket does not require authentication
			var auth = this.bucketName == null
						? null
						: new PlainTextAuthenticator(null, this.bucketName, this.bucketPassword);

			var state = (config == null || config.vBucketServerMap == null)
							? this.InitBasic(config, auth)
							: this.InitVBucket(config, auth);

			var nodes = state.CurrentNodes;

			state.Locator.Initialize(nodes);

			// we need to subscribe the failed event, 
			// so we can periodically check the dead 
			// nodes, since we do not get a config 
			// update every time a node dies
			for (var i = 0; i < nodes.Length; i++) nodes[i].Failed += this.NodeFail;

			Interlocked.Exchange(ref this.state, state);

			// kill the old nodes
			if (oldNodes != null)
				for (var i = 0; i < oldNodes.Length; i++)
					try
					{
						oldNodes[i].Failed -= this.NodeFail;
						oldNodes[i].Dispose();
					}
					catch { }
		}

		private InternalState InitVBucket(ClusterConfig config, ISaslAuthenticationProvider auth)
		{
			// we have a vbucket config, which has its own server list
			// it's supposed to be the same as the cluster config's list,
			// but the order is significicant (because of the bucket indexes),
			// so we we'll use this for initializing the locator
			var vbsm = config.vBucketServerMap;

			if (log.IsInfoEnabled) log.Info("Has vbucket. Server count: " + (vbsm.serverList == null ? 0 : vbsm.serverList.Length));

			var endpoints = (from server in vbsm.serverList
							 let parts = server.Split(':')
							 select new IPEndPoint(IPAddress.Parse(parts[0]), Int32.Parse(parts[1])));

			var epa = endpoints.ToArray();
			var buckets = vbsm.vBucketMap.Select(a => new VBucket(a[0], a.Skip(1).ToArray())).ToArray();
			var bucketNodeMap = buckets.ToLookup(vb => epa[vb.Master]);
			var vbnl = new VBucketNodeLocator(vbsm.hashAlgorithm, buckets);

			return new InternalState
			{
				CurrentNodes = endpoints.Select(ip => (IMemcachedNode)new BinaryNode(ip, this.configuration.SocketPool, auth)).ToArray(),
				Locator = vbnl,
				OpFactory = new VBucketAwareOperationFactory(vbnl)
			};
		}

		private InternalState InitBasic(ClusterConfig config, ISaslAuthenticationProvider auth)
		{
			if (log.IsInfoEnabled) log.Info("No vbucket. Server count: " + (config.nodes == null ? 0 : config.nodes.Length));

			// no vbucket config, use the node list and the ports
			var portType = this.configuration.Port;

			var tmp = config == null
					? Enumerable.Empty<IMemcachedNode>()
						: (from node in config.nodes
						   let ip = new IPEndPoint(IPAddress.Parse(node.hostname),
													(portType == BucketPortType.Proxy
														? node.ports.proxy
														: node.ports.direct))
						   where node.status == "healthy"
						   select (IMemcachedNode)(new BinaryNode(ip, this.configuration.SocketPool, auth)));

			return new InternalState
			{
				CurrentNodes = tmp.ToArray(),
				Locator = this.configuration.CreateNodeLocator() ?? new KetamaNodeLocator(),
				OpFactory = new Enyim.Caching.Memcached.Protocol.Binary.BinaryOperationFactory()
			};
		}

		void IDisposable.Dispose()
		{
			if (this.state != null)
				lock (this.RezSync)
				{
					if (this.state != null)
					{
						var currentNodes = this.state.CurrentNodes;
						this.state = null;

						this.configListener.Stop();
						this.configListener = null;

						if (this.resurrectTimer != null)
							using (this.resurrectTimer)
								this.resurrectTimer.Change(Timeout.Infinite, Timeout.Infinite);

						this.resurrectTimer = null;

						// close the pools
						if (currentNodes != null)
							for (var i = 0; i < currentNodes.Length; i++)
								currentNodes[i].Dispose();
					}
				}
		}

		private void rezCallback(object o)
		{
			if (this.state == null) return;

			if (log.IsDebugEnabled) log.Debug("Checking the dead servers.");

			// how this works:
			// 1. timer is created but suspended
			// 2. Locate encounters a dead server, so it starts the timer which will trigger after deadTimeout has elapsed
			// 3. if another server goes down before the timer is triggered, nothing happens in Locate (isRunning == true).
			//		however that server will be inspected sooner than Dead Timeout.
			//		   S1 died   S2 died    dead timeout
			//		|----*--------*------------*-
			//           |                     |
			//          timer start           both servers are checked here
			// 4. we iterate all the servers and record it in another list
			// 5. if we found a dead server whihc responds to Ping(), the locator will be reinitialized
			// 6. if at least one server is still down (Ping() == false), we restart the timer
			// 7. if all servers are up, we set isRunning to false, so the timer is suspended
			// 8. GOTO 2
			lock (this.RezSync)
			{
				var nodes = this.state.CurrentNodes;
				var aliveList = new List<IMemcachedNode>(nodes.Length);
				var deadCount = 0;

				for (var i = 0; i < nodes.Length; i++)
				{
					var n = nodes[i];
					if (n.IsAlive)
					{
						if (log.IsDebugEnabled) log.DebugFormat("Alive: {0}", n.EndPoint);
					}
					else
					{
						if (log.IsDebugEnabled) log.DebugFormat("Dead: {0}", n.EndPoint);

						if (n.Ping())
						{
							if (log.IsDebugEnabled) log.Debug("Ping ok.");
						}
						else
						{
							if (log.IsDebugEnabled) log.Debug("Still dead.");

							deadCount++;
						}
					}
				}

				// stop or restart the timer
				if (deadCount == 0)
				{
					if (log.IsDebugEnabled) log.Debug("deadCount == 0, stopping the timer.");

					this.isTimerActive = false;
				}
				else
				{
					if (log.IsDebugEnabled) log.DebugFormat("deadCount == {0}, starting the timer.", deadCount);

					this.resurrectTimer.Change(this.deadTimeoutMsec, Timeout.Infinite);
				}
			}
		}

		private void NodeFail(IMemcachedNode node)
		{
			if (log.IsDebugEnabled) log.DebugFormat("Node {0} is dead, starting the timer.", node.EndPoint);

			// the timer is stopped until we encounter the first dead server
			// when we have one, we trigger it and it will run after DeadTimeout has elapsed
			if (!this.isTimerActive)
				lock (this.RezSync)
					if (!this.isTimerActive)
					{
						if (this.resurrectTimer == null)
							this.resurrectTimer = new Timer(this.rezCallback, null, this.deadTimeoutMsec, Timeout.Infinite);
						else
							this.resurrectTimer.Change(this.deadTimeoutMsec, Timeout.Infinite);

						this.isTimerActive = true;
						if (log.IsDebugEnabled) log.Debug("Timer started.");
					}
		}

		#region [ IServerPool                  ]

		IMemcachedNode IServerPool.Locate(string key)
		{
			return this.state.Locator.Locate(key);
		}

		IOperationFactory IServerPool.OperationFactory
		{
			get { return this.state.OpFactory; }
		}

		IEnumerable<IMemcachedNode> IServerPool.GetWorkingNodes()
		{
			return this.state.Locator.GetWorkingNodes();
		}

		void IServerPool.Start()
		{
			// get the pool urls
			this.poolUrls = this.configuration.Urls.ToArray();
			if (this.poolUrls.Length == 0)
				throw new InvalidOperationException("At least 1 pool url must be specified.");

			this.configListener = new BucketConfigListener(this.poolUrls, this.bucketName, this.configuration.Credentials)
			{
				Timeout = (int)this.configuration.SocketPool.ConnectionTimeout.TotalMilliseconds,
				DeadTimeout = (int)this.configuration.SocketPool.DeadTimeout.TotalMilliseconds
			};

			this.configListener.ClusterConfigChanged += this.InitNodes;

			// start blocks until the first NodeListChanged event is triggered
			this.configListener.Start();
		}

		#endregion
		#region [ InternalState                ]

		private class InternalState
		{
			public static readonly InternalState Empty = new InternalState { CurrentNodes = new IMemcachedNode[0], Locator = new NotFoundLocator() };

			public IMemcachedNodeLocator Locator;
			public VBucketNodeLocator ForwardLocator;
			public IOperationFactory OpFactory;
			public IMemcachedNode[] CurrentNodes;
		}


		#endregion
		#region [ NotFoundLocator              ]

		private class NotFoundLocator : IMemcachedNodeLocator
		{
			public static readonly IMemcachedNodeLocator Instance = new NotFoundLocator();

			void IMemcachedNodeLocator.Initialize(IList<IMemcachedNode> nodes)
			{
			}

			IMemcachedNode IMemcachedNodeLocator.Locate(string key)
			{
				return null;
			}

			IEnumerable<IMemcachedNode> IMemcachedNodeLocator.GetWorkingNodes()
			{
				return Enumerable.Empty<IMemcachedNode>();
			}
		}

		#endregion
	}
}

#region [ License information          ]
/* ************************************************************
 * 
 *    Copyright (c) 2010 Attila Kiskó, enyim.com
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