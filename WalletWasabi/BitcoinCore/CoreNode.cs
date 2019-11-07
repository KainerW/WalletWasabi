using NBitcoin;
using NBitcoin.DataEncoders;
using NBitcoin.Protocol;
using NBitcoin.RPC;
using Nito.AsyncEx;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Helpers;

namespace WalletWasabi.BitcoinCore
{
	public class CoreNode
	{
		public EndPoint P2pEndPoint { get; private set; }
		public EndPoint RpcEndPoint { get; private set; }
		public RPCClient RpcClient { get; private set; }
		private BitcoindProcessBridge Bridge { get; set; }
		public Process Process { get; private set; }
		public string DataDir { get; private set; }

		public static async Task<CoreNode> CreateAsync(string dataDir)
		{
			var coreNode = new CoreNode();
			coreNode.DataDir = Guard.NotNullOrEmptyOrWhitespace(nameof(dataDir), dataDir);

			var configPath = Path.Combine(coreNode.DataDir, "bitcoin.conf");
			if (File.Exists(configPath))
			{
				var foundConfig = await NodeConfigParameters.LoadAsync(configPath);
				var rpcPortString = foundConfig["regtest.rpcport"];
				var rpcUser = foundConfig["regtest.rpcuser"];
				var rpcPassword = foundConfig["regtest.rpcpassword"];
				var pidFileName = foundConfig["regtest.pid"];
				var credentials = new NetworkCredential(rpcUser, rpcPassword);
				try
				{
					var rpc = new RPCClient(credentials, new Uri("http://127.0.0.1:" + rpcPortString + "/"), Network.RegTest);
					await rpc.StopAsync();

					var pidFile = Path.Combine(coreNode.DataDir, "regtest", pidFileName);
					if (File.Exists(pidFile))
					{
						var pid = await File.ReadAllTextAsync(pidFile);
						using var process = Process.GetProcessById(int.Parse(pid));
						await process.WaitForExitAsync(CancellationToken.None);
					}
					else
					{
						var allProcesses = Process.GetProcesses();
						var bitcoindProcesses = allProcesses.Where(x => x.ProcessName.Contains("bitcoind"));
						if (bitcoindProcesses.Count() == 1)
						{
							var bitcoind = bitcoindProcesses.First();
							await bitcoind.WaitForExitAsync(CancellationToken.None);
						}
					}
				}
				catch (Exception)
				{
				}
			}

			await IoHelpers.DeleteRecursivelyWithMagicDustAsync(coreNode.DataDir);
			IoHelpers.EnsureDirectoryExists(coreNode.DataDir);

			var pass = Encoders.Hex.EncodeData(RandomUtils.GetBytes(20));
			var creds = new NetworkCredential(pass, pass);

			var portArray = new int[2];
			var i = 0;
			while (i < portArray.Length)
			{
				var port = RandomUtils.GetUInt32() % 4000;
				port += 10000;
				if (portArray.Any(p => p == port))
				{
					continue;
				}

				try
				{
					var listener = new TcpListener(IPAddress.Loopback, (int)port);
					listener.Start();
					listener.Stop();
					portArray[i] = (int)port;
					i++;
				}
				catch (SocketException)
				{
				}
			}

			var p2pPort = portArray[0];
			var rpcPort = portArray[1];
			coreNode.P2pEndPoint = new IPEndPoint(IPAddress.Loopback, p2pPort);
			coreNode.RpcEndPoint = new IPEndPoint(IPAddress.Loopback, rpcPort);

			coreNode.RpcClient = new RPCClient($"{creds.UserName}:{creds.Password}", coreNode.RpcEndPoint.ToString(rpcPort), Network.RegTest);

			var config = new NodeConfigParameters
			{
				{"regtest", "1"},
				{"regtest.rest", "1"},
				{"regtest.listenonion", "0"},
				{"regtest.server", "1"},
				{"regtest.txindex", "1"},
				{"regtest.rpcuser", coreNode.RpcClient.CredentialString.UserPassword.UserName},
				{"regtest.rpcpassword", coreNode.RpcClient.CredentialString.UserPassword.Password},
				{"regtest.whitebind", "127.0.0.1:" + p2pPort.ToString()},
				{"regtest.rpcport", coreNode.RpcEndPoint.GetPortOrDefault().ToString()},
				{"regtest.printtoconsole", "0"}, // Set it to one if do not mind loud debug logs
				{"regtest.keypool", "10"},
				{"regtest.pid", "bitcoind.pid"}
			};

			await File.WriteAllTextAsync(configPath, config.ToString());
			using (await coreNode.KillerLock.LockAsync())
			{
				coreNode.Bridge = new BitcoindProcessBridge();
				coreNode.Process = coreNode.Bridge.Start($"-conf=bitcoin.conf -datadir={coreNode.DataDir} -debug=1", false);
				string pidFile = Path.Combine(coreNode.DataDir, "regtest", "bitcoind.pid");
				if (!File.Exists(pidFile))
				{
					Directory.CreateDirectory(Path.Combine(coreNode.DataDir, "regtest"));
					await File.WriteAllTextAsync(pidFile, coreNode.Process.Id.ToString());
				}
			}
			while (true)
			{
				try
				{
					await coreNode.RpcClient.GetBlockHashAsync(0);
					break;
				}
				catch
				{
				}
				if (coreNode.Process is null || coreNode.Process.HasExited)
				{
					break;
				}
			}

			return coreNode;
		}

		public static async Task<Version> GetVersionAsync(CancellationToken cancel)
		{
			var arguments = "-version";
			var bridge = new BitcoindProcessBridge();
			var (responseString, exitCode) = await bridge.SendCommandAsync(arguments, false, cancel).ConfigureAwait(false);

			if (exitCode != 0)
			{
				throw new BitcoindException($"'bitcoind {arguments}' exited with incorrect exit code: {exitCode}.");
			}
			var firstLine = responseString.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).First();
			var versionString = firstLine.TrimStart("Bitcoin Core Daemon version v", StringComparison.OrdinalIgnoreCase);
			var version = new Version(versionString);
			return version;
		}

		public async Task<Node> CreateP2pNodeAsync()
		{
			return await Node.ConnectAsync(Network.RegTest, P2pEndPoint);
		}

		public async Task<IEnumerable<Block>> GenerateAsync(int blockCount)
		{
			var blocks = await RpcClient.GenerateAsync(blockCount);
			var rpc = RpcClient.PrepareBatch();
			var tasks = blocks.Select(b => rpc.GetBlockAsync(b));
			rpc.SendBatch();
			return await Task.WhenAll(tasks);
		}

		private readonly AsyncLock KillerLock = new AsyncLock();

		public async Task StopAsync()
		{
			try
			{
				using (await KillerLock.LockAsync())
				{
					await RpcClient.StopAsync();
					using var timeout = new CancellationTokenSource(20000);
					await Process.WaitForExitAsync(timeout.Token);
					await IoHelpers.DeleteRecursivelyWithMagicDustAsync(DataDir);
				}
			}
			catch (Exception ex)
			{
				Logging.Logger.LogWarning(ex);
			}
		}
	}
}