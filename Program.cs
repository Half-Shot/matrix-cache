using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Matrix.AppService;
using Matrix.Structures;
using StackExchange.Redis;

namespace matrix_cache
{
    static class Program
    {
        const string REDIS_URL = "localhost";
        const string FRONTEND_URL = "http://localhost:6969";
        static readonly Regex joinedRoomsRegex = new Regex("/_matrix/client/r0/rooms/(.+)/joined_members?/");
        private static ConnectionMultiplexer redis;
        private static MatrixAppservice appservice;
        private static IDatabase db;
        private static HttpListener _listener;
        private static Semaphore _acceptSemaphore;

        static void Main(string[] args)
        {
            Console.WriteLine($"Connecting to Redis at {REDIS_URL}");
            try
            {
                redis = ConnectionMultiplexer.Connect(REDIS_URL);
                db = redis.GetDatabase();
            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed to start redis: {0}", ex);
                redis?.Close();
                Environment.Exit(1);
            }
            try
            {
                var opts = new ServiceRegistrationOptions
                {
                    sender_localpart = "mc",
                    as_token = "foobar",
                    hs_token = "foobar",
                    id = "matrix-cached",
                    url = "http://localhost:5000",
                    namespaces = new ServiceRegistrationOptionsNamespaces
                    {
                        aliases = new List<AppServiceNamespace>(),
                        users = new List<AppServiceNamespace>(),
                        rooms = new List<AppServiceNamespace>(),
                    }
                };
                
                opts.namespaces.aliases.Add(new AppServiceNamespace
                {
                    exclusive = true,
                    regex = "#_xmpp_.*",
                });
                opts.namespaces.users.Add(new AppServiceNamespace
                {
                    exclusive = true,
                    regex = "@_xmpp_.*",
                });
                var reg = new ServiceRegistration(opts);
                appservice = new MatrixAppservice(reg, "localhost", "http://localhost:8008");
                appservice.OnEvent += AppserviceOnOnEvent;
                Thread t = new Thread(listenForRequests);
                t.Start();
                appservice.Run();
                t.Join();
            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed to start appservice: {0}", ex);
                redis.Close();
                Environment.Exit(1);
            }
        }

        private static void listenForRequests()
        {
            _listener = new HttpListener ();
            _listener.Prefixes.Add (FRONTEND_URL+"/_matrix/client/r0/");
            _listener.Start();
            _acceptSemaphore = new Semaphore (64, 64);
            while(_listener.IsListening){
                _acceptSemaphore.WaitOne ();
                _listener.GetContextAsync ().ContinueWith (OnContext);
            }
        }

        private static void OnContext(Task<HttpListenerContext> obj)
        {
            var req = obj.Result.Request;
            var res = obj.Result.Response;
            res.ContentType = "application/json";
            res.StatusCode = 404;
            string result = "{\"error\": \"Not found\"}";
            var match = joinedRoomsRegex.Match(req.Url.AbsolutePath);
            if (match.Success)
            {
                HandleJoinedUsersRequest(match.Groups[1].Value, out result);
                res.StatusCode = 200;
            }
            res.OutputStream.Write(Encoding.UTF8.GetBytes(result));
            res.OutputStream.Flush();
            res.Close();
        }

        private static void HandleStateRequest()
        {
            
        }

        private static void HandleJoinedUsersRequest(string roomId, out string result)
        {
            var members = db.SetMembers($"{roomId}:membership.join");
            result = "{\"joined\":{";
            foreach (var redisValue in members)
            {
                result += $"\"{redisValue}\":{{}},";
            }
            result = result.Substring(0,result.Length - 1) + "}}";
        }

        private static async void AppserviceOnOnEvent(MatrixEvent ev)
        {
            if (ev.type == MatrixEventType.RoomMember)
            {
                MatrixMRoomMember member = ev.content as MatrixMRoomMember;
                if (member?.membership == EMatrixRoomMembership.Join)
                {
                    await db.SetAddAsync($"{ev.room_id}:membership.join", ev.sender);
                } else if (member?.membership == EMatrixRoomMembership.Leave ||
                           member?.membership == EMatrixRoomMembership.Ban)
                {
                    await db.SetRemoveAsync($"{ev.room_id}:membership.join", ev.sender);
                }
            }
            var hasCompleteState = await db.StringGetBitAsync($"{ev.room_id}:membership.complete", 0);
            if (!hasCompleteState)
            {
                var members = appservice.GetClientAsUser().Api.GetJoinedMembers(ev.room_id).Keys;
                var b = db.CreateBatch();
                foreach (var userId in members)
                {
                    b.SetAddAsync($"{ev.room_id}:membership.join", userId);
                }
                b.Execute();
                db.StringSetBit($"{ev.room_id}:membership.complete", 0, true);
            }
        }
    }
}
