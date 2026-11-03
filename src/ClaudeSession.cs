using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace LazyReply
{
    // Claude Code processes kept running ("claude -p --input-format stream-json"). Each request is a
    // new user turn on standard input, so the start-up of the tool (1–2 s, more for the first answer)
    // is paid once per process instead of once per message.
    //
    // Two processes per set of arguments: a turn cannot be stopped once sent, so while one process
    // finishes an answer the user no longer needs (they typed on), the other takes the new message at
    // once. A process is replaced after MaxTurns turns, so its conversation history stays short.
    // The answer streams in: Ask reports the text received so far, so the first version can be shown
    // before the last one is written.
    class ClaudeSession
    {
        const int MaxTurns = 30;
        const int PerArguments = 2;
        const int AnswerTimeoutMs = 90000;

        static readonly Dictionary<string, List<ClaudeSession>> pools = new Dictionary<string, List<ClaudeSession>>();
        static readonly object gate = new object();
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        readonly Process proc;
        readonly StreamWriter input;
        readonly SemaphoreSlim turn = new SemaphoreSlim(1, 1);
        readonly StringBuilder streamed = new StringBuilder();
        TaskCompletionSource<Dictionary<string, object>> pending;
        Action<string> onPartial;
        volatile bool dead, busy;
        int turns;
        string lastError = "";

        bool Usable { get { return !dead && turns < MaxTurns; } }

        // An idle process for these arguments; a new one while there are fewer than PerArguments;
        // otherwise the one that took the fewest turns (the caller then waits for it).
        public static ClaudeSession For(string exe, string args)
        {
            lock (gate)
            {
                List<ClaudeSession> pool;
                if (!pools.TryGetValue(args, out pool)) { pool = new List<ClaudeSession>(); pools[args] = pool; }
                foreach (var old in pool.Where(s => !s.Usable && !s.busy).ToList()) { old.Stop(); pool.Remove(old); }
                var idle = pool.FirstOrDefault(s => s.Usable && !s.busy);
                if (idle != null) return idle;
                if (pool.Count < PerArguments)
                {
                    var created = new ClaudeSession(exe, args);
                    pool.Add(created);
                    return created;
                }
                return pool.Where(s => s.Usable).OrderBy(s => s.turns).FirstOrDefault() ?? Replace(pool, exe, args);
            }
        }

        static ClaudeSession Replace(List<ClaudeSession> pool, string exe, string args)
        {
            foreach (var s in pool) s.Stop();
            pool.Clear();
            var created = new ClaudeSession(exe, args);
            pool.Add(created);
            return created;
        }

        // Starts every process of the pool ahead of time, so even the first messages are fast.
        public static void Prepare(string exe, string args)
        {
            lock (gate)
            {
                List<ClaudeSession> pool;
                if (!pools.TryGetValue(args, out pool)) { pool = new List<ClaudeSession>(); pools[args] = pool; }
                while (pool.Count(s => s.Usable) < PerArguments) pool.Add(new ClaudeSession(exe, args));
            }
        }

        public static void StopAll()
        {
            lock (gate)
            {
                foreach (var pool in pools.Values) foreach (var s in pool) s.Stop();
                pools.Clear();
            }
        }

        ClaudeSession(string exe, string args)
        {
            Directory.CreateDirectory(Settings.Dir);
            var psi = new ProcessStartInfo(exe, "-p --input-format stream-json --output-format stream-json --verbose --include-partial-messages " + args)
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Settings.Dir,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            proc = Process.Start(psi);
            input = new StreamWriter(proc.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true };
            proc.ErrorDataReceived += (o, e) => { if (!string.IsNullOrEmpty(e.Data)) lastError = e.Data; };
            proc.BeginErrorReadLine();
            new Thread(ReadLoop) { IsBackground = true, Name = "claude-session" }.Start();
            Log.Write("claude session started");
            Warm();
        }

        // The first answer of a new process is the slow one; a throw-away turn takes that cost
        // while the user is still typing.
        void Warm()
        {
            Task.Run(async delegate
            {
                try { await Ask("<message>\nok\n</message>", CancellationToken.None, null); }
                catch { }
            });
        }

        void ReadLoop()
        {
            try
            {
                string line;
                while ((line = proc.StandardOutput.ReadLine()) != null)
                {
                    Dictionary<string, object> ev = null;
                    try { ev = Json.DeserializeObject(line) as Dictionary<string, object>; } catch { }
                    if (ev == null) continue;
                    object type;
                    ev.TryGetValue("type", out type);
                    if ("stream_event".Equals(type)) { OnStreamEvent(ev); continue; }
                    if (!"result".Equals(type)) continue;
                    var p = Interlocked.Exchange(ref pending, null);
                    if (p != null) p.TrySetResult(ev);
                }
            }
            catch { }
            dead = true;
            var left = Interlocked.Exchange(ref pending, null);
            if (left != null) left.TrySetException(new EngineException("Claude Code থেমে গেছে: " + lastError));
            Log.Write("claude session ended");
        }

        // Text pieces of the answer as they are written (plain text or the JSON of the structured output).
        void OnStreamEvent(Dictionary<string, object> ev)
        {
            object e, d, piece;
            var inner = ev.TryGetValue("event", out e) ? e as Dictionary<string, object> : null;
            if (inner == null || !"content_block_delta".Equals(inner.ContainsKey("type") ? inner["type"] : null)) return;
            var delta = inner.TryGetValue("delta", out d) ? d as Dictionary<string, object> : null;
            if (delta == null) return;
            if (!delta.TryGetValue("text", out piece) && !delta.TryGetValue("partial_json", out piece)) return;
            streamed.Append(piece as string);
            var report = onPartial;
            if (report != null) { try { report(streamed.ToString()); } catch { } }
        }

        // One turn at a time per process. A caller that stops waiting (the user typed on) does not
        // stop the turn; the process is busy until its answer has arrived.
        public async Task<Dictionary<string, object>> Ask(string user, CancellationToken ct, Action<string> partial)
        {
            await turn.WaitAsync(ct);
            bool handedOver = false;
            try
            {
                if (dead) throw new EngineException("Claude Code থেমে গেছে। আবার চেষ্টা করুন।" + (lastError.Length > 0 ? " (" + lastError + ")" : ""));
                ct.ThrowIfCancellationRequested();
                var tcs = new TaskCompletionSource<Dictionary<string, object>>();
                streamed.Clear();
                onPartial = partial;
                pending = tcs;
                busy = true;
                turns++;
                var message = new Dictionary<string, object>
                {
                    { "type", "user" },
                    { "message", new Dictionary<string, object> { { "role", "user" }, { "content", user } } }
                };
                input.WriteLine(Json.Serialize(message));
                Task release = tcs.Task.ContinueWith(delegate { onPartial = null; busy = false; turn.Release(); });
                handedOver = true;

                var stop = new TaskCompletionSource<bool>();
                using (ct.Register(delegate { onPartial = null; stop.TrySetResult(true); }))
                {
                    Task first = await Task.WhenAny(tcs.Task, stop.Task, Task.Delay(AnswerTimeoutMs));
                    if (first == tcs.Task) return await tcs.Task;
                    if (first == stop.Task) throw new OperationCanceledException(ct);
                }
                Stop();
                throw new EngineException("উত্তর আসতে খুব দেরি হচ্ছে। আবার চেষ্টা করুন।");
            }
            finally
            {
                if (!handedOver) turn.Release();
            }
        }

        void Stop()
        {
            dead = true;
            try { input.Close(); } catch { }
            try { if (!proc.HasExited && !proc.WaitForExit(1500)) proc.Kill(); } catch { }
        }
    }
}
