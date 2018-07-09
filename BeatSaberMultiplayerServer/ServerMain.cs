﻿using BeatSaberMultiplayerServer.Misc;
using Newtonsoft.Json;
using ServerCommons.Data;
using ServerCommons.Misc;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using Math = ServerCommons.Misc.Math;
using Settings = BeatSaberMultiplayerServer.Misc.Settings;
using Logger = ServerCommons.Misc.Logger;
using WebSocketSharp;
using WebSocketSharp.Server;

namespace BeatSaberMultiplayerServer
{
    public class Broadcast : WebSocketBehavior
    {
        protected override void OnOpen()
        {
            base.OnOpen();
            Logger.Instance.Log("WebSocket Client Connected!");
        }
    }

    class ServerMain
    {
        static TcpListener _listener;

        public static List<Client> clients = new List<Client>();

        public static ServerState serverState = ServerState.Lobby;
        public static List<CustomSongInfo> availableSongs = new List<CustomSongInfo>();

        public static int currentSongIndex = -1;
        private static int lastSelectedSong = -1;

        private static string TitleFormat = "{0} - {1} Clients Connected";

        public static TimeSpan playTime = new TimeSpan();

        static List<ServerHubClient> _serverHubClients = new List<ServerHubClient>();

        private Thread ListenerThread { get; set; }
        private Thread ServerLoopThread { get; set; }

        public WebSocketServer wss;

        static void Main(string[] args) => new ServerMain().Start(args);

        public void Start(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += HandleUnhandledException;

            Console.Title = string.Format(TitleFormat, Settings.Instance.Server.ServerName, 0);
            Logger.Instance.Log($"Beat Saber Multiplayer Server v{Assembly.GetEntryAssembly().GetName().Version}");

            VersionChecker.CheckForUpdates();

            if (args.Length > 0)
            {
                try
                {
                    Settings.Instance.Server.Port = int.Parse(args[0]);
                }
                catch (Exception e)
                {
                    Logger.Instance.Exception($"Can't parse argumnets! Exception: {e}");
                }
            }

            Logger.Instance.Log($"Hosting Server @ {Settings.Instance.Server.IP}:{Settings.Instance.Server.Port}");

            Logger.Instance.Log("Downloading songs from BeatSaver...");
            DownloadSongs();


            Logger.Instance.Log("Starting server...");
            _listener = new TcpListener(IPAddress.Any, Settings.Instance.Server.Port);

            _listener.Start();

            Logger.Instance.Log("Waiting for clients...");

            ListenerThread = new Thread(AcceptClientThread) { IsBackground = true };
            ListenerThread.Start();

            ServerLoopThread = new Thread(ServerLoop) { IsBackground = true };
            ServerLoopThread.Start();

            if (Settings.Instance.Server.WSEnabled)
            {
                Logger.Instance.Log($"WebSocket Server started @ {Settings.Instance.Server.IP}:{Settings.Instance.Server.WSPort}");
                wss = new WebSocketServer(Settings.Instance.Server.WSPort);
                wss.AddWebSocketService<Broadcast>("/");
                wss.Start();
            }
            
            Dictionary<string, int> _serverHubs = new Dictionary<string, int>();
                
            for (int i = 0; i < Settings.Instance.Server.ServerHubIPs.Length; i++)
            {
                if(Settings.Instance.Server.ServerHubPorts.Length >= i)
                {
                    _serverHubs.Add(Settings.Instance.Server.ServerHubIPs[i], 3700);
                }
                else
                {
                    _serverHubs.Add(Settings.Instance.Server.ServerHubIPs[i], Settings.Instance.Server.ServerHubPorts[i]);
                }
            }

            _serverHubs.AsParallel().ForAll(x =>
            {
                ServerHubClient client = new ServerHubClient();
                _serverHubClients.Add(client);

                client.Connect(x.Key, x.Value);
            }
            );

            ShutdownEventCatcher.Shutdown += OnServerShutdown;

            Logger.Instance.Warning($"Use [Help] to display commands");
            Logger.Instance.Warning($"Use [Quit] to exit");
            while (Thread.CurrentThread.IsAlive)
            {
                var x = Console.ReadLine();
                if (x == string.Empty) continue;
                var comParts = x?.Split(' ');
                var comName = comParts[0];
                var comArgs = comParts.Skip(1).ToArray();
                string s = string.Empty;
                switch (comName.ToLower())
                {
                    case "help":
                        foreach (var com in new[] { "help", "quit", "clients", "blacklist [add/remove] [playerID/IP]", "whitelist [enable/disable/add/remove] [playerID/IP]" })
                        {
                            s += $"{Environment.NewLine}> {com}";
                        }

                        Logger.Instance.Log($"Commands:{s}");
                        break;
                    case "quit":
                        Environment.Exit(0);
                        return;
                    case "clients":
                        foreach (var t in clients)
                        {
                            var client = t.playerInfo;
                            if (t.playerInfo == null)
                            {
                                s +=
                                    $"{Environment.NewLine}[{t.state}] NOT AVAILABLE @ {((IPEndPoint)t._client.Client.RemoteEndPoint).Address}";
                            }
                            else
                            {
                                s +=
                                    $"{Environment.NewLine}[{t.state}] {client.playerName} @ {((IPEndPoint)t._client.Client.RemoteEndPoint).Address}";
                            }
                        }

                        if (s == String.Empty) s = " No Clients";
                        Logger.Instance.Log($"Connected Clients:{s}");
                        break;
                    case "blacklist":
                        {
                            if (comArgs.Length == 2 && !comArgs[1].IsNullOrEmpty())
                            {
                                switch (comArgs[0])
                                {
                                    case "add":
                                        {
                                            clients.Where(y => y.clientIP == comArgs[1] || y.playerId.ToString() == comArgs[1]).AsParallel().ForAll(z => z.KickClient());
                                            Settings.Instance.Access.Blacklist.Add(comArgs[1]);
                                            Logger.Instance.Log($"Successfully banned {comArgs[1]}");
                                            Settings.Instance.Save();
                                        }
                                        break;
                                    case "remove":
                                        {
                                            if (Settings.Instance.Access.Blacklist.Remove(comArgs[1]))
                                            {
                                                Logger.Instance.Log($"Successfully unbanned {comArgs[1]}");
                                                Settings.Instance.Save();
                                            }
                                            else
                                            {
                                                Logger.Instance.Warning($"{comArgs[1]} is not banned");
                                            }
                                        }
                                        break;
                                    default:
                                        {
                                            Logger.Instance.Warning($"Command usage: blacklist [add/remove] [playerID/IP]");
                                        }
                                        break;
                                }
                            }
                            else
                            {
                                Logger.Instance.Warning($"Command usage: blacklist [add/remove] [playerID/IP]");
                            }
                        }
                        break;
                    case "whitelist":
                        {
                            if (comArgs.Length >= 1)
                            {
                                switch (comArgs[0])
                                {
                                    case "enable":
                                        {
                                            Settings.Instance.Access.WhitelistEnabled = true;
                                            Logger.Instance.Log($"Whitelist enabled");
                                            Settings.Instance.Save();
                                        }
                                        break;
                                    case "disable":
                                        {
                                            Settings.Instance.Access.WhitelistEnabled = false;
                                            Logger.Instance.Log($"Whitelist disabled");
                                            Settings.Instance.Save();
                                        }
                                        break;
                                    case "add":
                                        {
                                            if (comArgs.Length == 2 && !comArgs[1].IsNullOrEmpty())
                                            {
                                                Settings.Instance.Access.Whitelist.Add(comArgs[1]);
                                                Logger.Instance.Log($"Successfully whitelisted {comArgs[1]}");
                                                Settings.Instance.Save();
                                            }
                                            else
                                            {
                                                Logger.Instance.Warning($"Command usage: whitelist [enable/disable/add/remove] [playerID/IP]");
                                            }
                                        }
                                        break;
                                    case "remove":
                                        {
                                            if (comArgs.Length == 2 && !comArgs[1].IsNullOrEmpty())
                                            {
                                                clients.Where(y => y.clientIP == comArgs[1] || y.playerId.ToString() == comArgs[1]).AsParallel().ForAll(z => z.KickClient());
                                                if (Settings.Instance.Access.Whitelist.Remove(comArgs[1]))
                                                {
                                                    Logger.Instance.Log($"Successfully removed {comArgs[1]} from whitelist");
                                                    Settings.Instance.Save();
                                                }
                                                else
                                                {
                                                    Logger.Instance.Warning($"{comArgs[1]} is not whitelisted");
                                                }
                                            }
                                            else
                                            {
                                                Logger.Instance.Warning($"Command usage: whitelist [enable/disable/add/remove] [playerID/IP]");
                                            }
                                        }
                                        break;
                                    default:
                                        {
                                            Logger.Instance.Warning($"Command usage: whitelist [enable/disable/add/remove] [playerID/IP]");
                                        }
                                        break;
                                }
                            }
                            else
                            {
                                Logger.Instance.Warning($"Command usage: whitelist [enable/disable/add/remove] [playerID/IP]");
                            }


                        }
                        break;
                    case "crash":
                        throw new Exception("DebugException");
                }

            }
        }
        
        private static void DownloadSongs()
        {
            Settings.Instance.Server.Downloaded.GetDirectories().AsParallel().ForAll(dir => dir.Delete(true));

            Settings.Instance.AvailableSongs.Songs.ToList().ForEach(id =>
            {
                var zipPath = Path.Combine(Settings.Instance.Server.Downloads.FullName, $"{id}.zip");
                Thread.Sleep(25);
                using (var client = new WebClient())
                {
                    client.Headers.Add("user-agent",
                        $"BeatSaberMultiplayerServer-{Assembly.GetEntryAssembly().GetName().Version}");
                    if (Settings.Instance.Server.Downloads.GetFiles().All(o => o.Name != $"{id}.zip"))
                    {
                        Logger.Instance.Log($"Downloading {id}.zip");
                        client.DownloadFile($"https://beatsaver.com/dl.php?id={id}", zipPath);
                    }
                }

                ZipArchive zip = null;
                try
                {
                    zip = ZipFile.OpenRead(zipPath);
                }
                catch (Exception ex)
                {
                    Logger.Instance.Exception(ex.Message);
                }

                var songName = zip?.Entries[0].FullName.Split('/')[0];
                try
                {
                    zip?.ExtractToDirectory(Settings.Instance.Server.Downloaded.FullName);
                    try
                    {
                        zip?.Dispose();
                    }
                    catch (IOException)
                    {
                        Logger.Instance.Exception($"Failed to remove Zip [{id}]");
                    }
                }
                catch (IOException)
                {
                    Logger.Instance.Exception($"Folder [{songName}] exists. Continuing.");
                    try
                    {
                        zip?.Dispose();
                    }
                    catch (IOException)
                    {
                        Logger.Instance.Exception($"Failed to remove Zip [{id}]");
                    }
                }
            });

            Logger.Instance.Log("All songs downloaded!");

            List<CustomSongInfo> _songs = SongLoader.RetrieveAllSongs();

            _songs.AsParallel().ForAll(song =>
            {
                try
                {
                    Logger.Instance.Log($"Processing {song.songName} {song.songSubName}");

                    using (NVorbis.VorbisReader vorbis =
                        new NVorbis.VorbisReader($"{song.path}/{song.difficultyLevels[0].audioPath}"))
                    {
                        song.duration = vorbis.TotalTime;
                    }

                    availableSongs.Add(song);
                }
                catch(AggregateException e)
                {
                    Logger.Instance.Error(e.Message);
                    Logger.Instance.Warning("One common cause of this is incorrect case sensitivity in the song's json file in comparison to its actual song name.");
                }
             });

            Logger.Instance.Log("Done!");
        }

        void ServerLoop()
        {
            Stopwatch _timer = new Stopwatch();
            _timer.Start();
            int _timerSeconds = 0;
            TimeSpan _lastTime = new TimeSpan();

            float lobbyTimer = 0;
            float sendTimer = 0;

            float sendTime = 1f / 20;

            int lobbyTime = Settings.Instance.Server.LobbyTime;

            TimeSpan deltaTime;

            while (ServerLoopThread.IsAlive)
            {
                deltaTime = (_timer.Elapsed - _lastTime);

                _lastTime = _timer.Elapsed;

                switch (serverState)
                {
                    case ServerState.Lobby:
                        {

                            sendTimer += (float)deltaTime.TotalSeconds;
                            lobbyTimer += (float)deltaTime.TotalSeconds;

                            if (clients.Count == 0)
                            {
                                lobbyTimer = 0;
                            }

                            if (Math.Ceiling(lobbyTimer) > _timerSeconds && _timerSeconds > -1)
                            {
                                _timerSeconds = Math.Ceiling(lobbyTimer);
                                SendToAllClients(JsonConvert.SerializeObject(
                                    new ServerCommand(ServerCommandType.SetLobbyTimer,
                                        Math.Max(lobbyTime - _timerSeconds, 0))));
                            }

                            if (sendTimer >= sendTime)
                            {
                                SendToAllClients(JsonConvert.SerializeObject(new ServerCommand(
                                    ServerCommandType.SetPlayerInfos,
                                    _playerInfos: (clients.Where(x => x.playerInfo != null)
                                        .Select(x => JsonConvert.SerializeObject(x.playerInfo))).ToArray()
                                    )));
                                sendTimer = 0f;
                            }


                            if (lobbyTimer >= lobbyTime / 2 && currentSongIndex == -1)
                            {
                                if (Settings.Instance.AvailableSongs.Shuffle)
                                {
                                    Random rand = new Random();
                                    currentSongIndex = rand.Next(availableSongs.Count);
                                    if(currentSongIndex == lastSelectedSong) currentSongIndex = lastSelectedSong + 1;
                                }
                                else
                                {
                                    currentSongIndex = lastSelectedSong + 1;
                                }

                                if (currentSongIndex >= availableSongs.Count)
                                {
                                    currentSongIndex = 0;
                                }

                                SendToAllClients(JsonConvert.SerializeObject(new ServerCommand(
                                    ServerCommandType.SetSelectedSong,
                                    _selectedLevelID: availableSongs[currentSongIndex].levelId,
                                    _difficulty: GetPreferredDifficulty(availableSongs[currentSongIndex]))), wss);
                            }

                            if (lobbyTimer >= lobbyTime)
                            {
                                SendToAllClients(JsonConvert.SerializeObject(new ServerCommand(
                                    ServerCommandType.SetSelectedSong,
                                    _selectedLevelID: availableSongs[currentSongIndex].levelId,
                                    _difficulty: GetPreferredDifficulty(availableSongs[currentSongIndex]))), wss);
                                SendToAllClients(
                                    JsonConvert.SerializeObject(
                                        new ServerCommand(ServerCommandType.StartSelectedSongLevel)), wss);

                                serverState = ServerState.Playing;
                                Logger.Instance.Log("Starting song " + availableSongs[currentSongIndex].songName + " " +
                                                    availableSongs[currentSongIndex].songSubName + "...");
                                _timerSeconds = 0;
                                lobbyTimer = 0;
                            }
                        };
                        break;
                    case ServerState.Playing:
                        {
                            sendTimer += (float)deltaTime.TotalSeconds;
                            playTime += deltaTime;

                            if (sendTimer >= sendTime)
                            {
                                SendToAllClients(JsonConvert.SerializeObject(new ServerCommand(
                                    ServerCommandType.SetPlayerInfos,
                                    _playerInfos: (clients.Where(x => x.playerInfo != null)
                                        .OrderByDescending(x => x.playerInfo.playerScore)
                                        .Select(x => JsonConvert.SerializeObject(x.playerInfo))).ToArray(),
                                    _selectedSongDuration: availableSongs[currentSongIndex].duration.TotalSeconds,
                                    _selectedSongPlayTime: playTime.TotalSeconds)), wss);
                                sendTimer = 0f;
                            }

                            if (playTime.TotalSeconds >= availableSongs[currentSongIndex].duration.TotalSeconds + 2.5f)
                            {
                                playTime = new TimeSpan();
                                sendTimer = 0f;
                                serverState = ServerState.Lobby;
                                lastSelectedSong = currentSongIndex;
                                currentSongIndex = -1;
                                Logger.Instance.Log("Returning to lobby...");
                            }

                            if (clients.Count(x => x.state == ClientState.Playing) == 0 && playTime.TotalSeconds > 5)
                            {
                                playTime = new TimeSpan();
                                sendTimer = 0f;
                                serverState = ServerState.Lobby;
                                lastSelectedSong = currentSongIndex;
                                currentSongIndex = -1;

                                Logger.Instance.Log("Returning to lobby (NO PLAYERS)...");
                            }
                        };
                        break;
                }
                
                Console.Title = string.Format(TitleFormat, Settings.Instance.Server.ServerName, clients.Count);
                Thread.Sleep(5);
            }
        }

        static int GetPreferredDifficulty(CustomSongInfo _song)
        {
            int difficulty = 0;

            foreach (CustomSongInfo.DifficultyLevel diff in _song.difficultyLevels)
            {
                if ((int)Enum.Parse(typeof(Difficulty), diff.difficulty) <= (int)Settings.Instance.Server.PreferredDifficulty &&
                    (int)Enum.Parse(typeof(Difficulty), diff.difficulty) >= difficulty)
                {
                    difficulty = (int)Enum.Parse(typeof(Difficulty), diff.difficulty);
                }
            }

            if (difficulty == 0 && _song.difficultyLevels.Length > 0)
            {
                difficulty = (int)Enum.Parse(typeof(Difficulty), _song.difficultyLevels[0].difficulty);
            }

            return difficulty;
        }

        static void SendToAllClients(string message, bool retryOnError = false)
        {
            try
            {
                clients.Where(x => x != null && (x.state == ClientState.Connected || x.state == ClientState.Playing))
                    .AsParallel().ForAll(x => { x.sendQueue.Enqueue(message); });
            }
            catch (Exception e)
            {
                Logger.Instance.Exception("Can't send message to all clients! Exception: " + e);
            }
        }

        static void SendToAllClients(string message, WebSocketServer wss, bool retryOnError = false)
        {
            try
            {
                clients.Where(x => x != null && (x.state == ClientState.Connected || x.state == ClientState.Playing))
                    .AsParallel().ForAll(x => { x.sendQueue.Enqueue(message); });
                
                if (wss != null)
                {
                    wss.WebSocketServices["/"].Sessions.Broadcast(message);
                }
            }
            catch (Exception e)
            {
                Logger.Instance.Exception("Can't send message to all clients! Exception: " + e);
            }
        }

        async void AcceptClientThread()
        {
            while (ListenerThread.IsAlive)
            {
                TcpClient client = await _listener.AcceptTcpClientAsync();
                ClientThread(client);
            }
        }

        static void ClientThread(TcpClient client)
        {
            clients.Add(new Client(client));
        }
        
        private void HandleUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Logger.Instance.Exception(e.ExceptionObject.ToString());
            Environment.FailFast("UnhadledException", e.ExceptionObject as Exception);
        }

        private void OnServerShutdown(ShutdownEventArgs args)
        {
            Logger.Instance.Log("Shutting down server...");

            _serverHubClients.AsParallel().ForAll(x => x.Disconnect());

            clients.AsParallel().ForAll(x => x.DestroyClient());

            _listener.Server.Shutdown(SocketShutdown.Both);
            _listener.Stop();
        }
    }

    class ServerHubClient
    {
        public string ip;
        public int port;

        TcpClient client;

        public int ID;
        
        public void Connect(string serverHubIP, int serverHubPort)
        {
            ip = serverHubIP;
            port = serverHubPort;

            try
            {
                client = new TcpClient(ip, port);

                ServerDataPacket packet = new ServerDataPacket
                {
                    ConnectionType = ConnectionType.Server,
                    FirstConnect = true,
                    IPv4 = Settings.Instance.Server.IP,
                    Port = Settings.Instance.Server.Port,
                    Name = Settings.Instance.Server.ServerName
                };

                byte[] packetBytes = packet.ToBytes();

                client.GetStream().Write(packetBytes, 0, packetBytes.Length);

                byte[] bytes = new byte[Packet.MAX_BYTE_LENGTH];
                if (client.GetStream().Read(bytes, 0, bytes.Length) != 0)
                {
                    packet = (ServerDataPacket)Packet.ToPacket(bytes);
                }

                ID = packet.ID;


                Logger.Instance.Log($"Connected to ServerHub @ {ip}");
            }
            catch(Exception e)
            {
                Logger.Instance.Warning($"Can't connect to ServerHub @ {ip}");
                Logger.Instance.Warning($"Exception: {e.Message}");
            }

        }

        public void Disconnect()
        {
            try
            {
                if (client != null && client.Connected)
                {
                    ServerDataPacket packet = new ServerDataPacket
                    {
                        ConnectionType = ConnectionType.Server,
                        ID = ID,
                        FirstConnect = false,
                        IPv4 = Settings.Instance.Server.IP,
                        Port = Settings.Instance.Server.Port,
                        Name = Settings.Instance.Server.ServerName,
                        RemoveFromCollection = true
                    };

                    client.GetStream().Write(packet.ToBytes(), 0, packet.ToBytes().Length);
                    Logger.Instance.Log($"Removed this server from ServerHub @ {ip}");

                    client.Close();
                }
            }catch(Exception e)
            {
                Logger.Instance.Warning($"Can't remove server from ServerHub @ {ip}");
                Logger.Instance.Warning($"Exception: {e.Message}");
            }
        }

    }
}