using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace GrimDawnModMerger
{
    /// <summary>
    /// Runs ArchiveTool / arzedit one command at a time on a background thread.
    /// Tools are started directly (no cmd.exe) with an argument list, so no user-controlled
    /// text is ever interpreted by a shell.
    /// </summary>
    public class CommandLine
    {
        public BlockingCollection<Message> messageQueue;
        public event EventHandler<UILogMessageEventArgs> UILogMessageEvent;

        private readonly string gameDir;
        private Thread cmdThread;
        private volatile bool cancelled;
        private Process currentProcess;
        private readonly object processLock = new object();

        private readonly HashSet<string> files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> overwrittenNames = new List<string>();
        private readonly object trackLock = new object();
        private int errorCount;

        public CommandLine(string gameDir, EventHandler<UILogMessageEventArgs> eventHandler)
        {
            this.gameDir = gameDir;
            messageQueue = new BlockingCollection<Message>(new ConcurrentQueue<Message>());
            UILogMessageEvent += eventHandler;
        }

        public void Start()
        {
            errorCount = 0;
            cmdThread = new Thread(ThreadLoop);
            cmdThread.IsBackground = true;
            cmdThread.Start();
        }

        /// <summary>
        /// gracefully: finish everything already queued, then stop.
        /// not gracefully: kill the running tool, skip anything still queued, then stop.
        /// </summary>
        public void Kill(bool gracefully = true)
        {
            if (!gracefully)
            {
                cancelled = true;
                lock (processLock)
                {
                    try
                    {
                        if (currentProcess != null && !currentProcess.HasExited)
                            currentProcess.Kill(true);
                    }
                    catch (InvalidOperationException) { /* already exited */ }
                }
            }

            messageQueue.Add(new Message { Command = "Close", Args = null });
        }

        public void ThreadLoop()
        {
            Trace.WriteLine("Thread " + Thread.CurrentThread.ManagedThreadId + " running...");
            while (true)
            {
                Message message = messageQueue.Take();
                if (message.Command == "Close")
                    break;
                if (cancelled)
                    continue; // drain the queue without running anything

                try
                {
                    switch (message.Command)
                    {
                        case "Extract":
                            Extract(message.Args[0], message.Args[1]);
                            break;
                        case "ExtractDatabase":
                            ExtractDatabase(message.Args[0], message.Args[1]);
                            break;
                        case "Pack":
                            Pack(message.Args[0], message.Args[1], message.Args[2]);
                            break;
                        case "BuildDatabase":
                            BuildDatabase(message.Args[0], message.Args[1]);
                            break;
                    }
                }
                catch (Exception e)
                {
                    errorCount++;
                    SendToUI("ERROR running " + message.Command + ": " + e.Message);
                }
            }

            Close();
        }

        private string ArchiveToolPath
        {
            get { return Path.Combine(gameDir, "ArchiveTool.exe"); }
        }

        private string FindArzEdit()
        {
            string[] candidates =
            {
                Path.Combine(AppContext.BaseDirectory, "arzedit.exe"),
                Path.Combine(Directory.GetCurrentDirectory(), "arzedit.exe")
            };
            foreach (string c in candidates)
                if (File.Exists(c))
                    return c;
            return candidates[0]; // reported as "not found" by RunTool
        }

        public void Extract(string from, string to)
        {
            RunTool(ArchiveToolPath, from, "-extract", to);
        }

        public void ExtractDatabase(string from, string to)
        {
            RunTool(ArchiveToolPath, from, "-database", to);
        }

        public void Pack(string parentDir, string dir, string to)
        {
            RunTool(ArchiveToolPath, to, "-update", ".", parentDir, "6");
        }

        public void BuildDatabase(string modDir, string gameDir)
        {
            RunTool(FindArzEdit(), "build", modDir, modDir,
                    "-g", gameDir,
                    "-t", Path.Combine(modDir, "database", "templates"),
                    "-A", "-R");
        }

        private void RunTool(string exePath, params string[] args)
        {
            if (!File.Exists(exePath))
            {
                errorCount++;
                SendToUI("ERROR: tool not found: " + exePath);
                return;
            }

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = gameDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string a in args)
                psi.ArgumentList.Add(a);

            using (var p = new Process { StartInfo = psi })
            {
                p.OutputDataReceived += (sender, e) =>
                {
                    if (e.Data == null) return;
                    TrackFile(e.Data);
                    SendToUI(e.Data);
                };
                p.ErrorDataReceived += (sender, e) =>
                {
                    if (e.Data != null) SendToUI(e.Data);
                };

                lock (processLock)
                {
                    if (cancelled) return;
                    p.Start();
                    currentProcess = p;
                }

                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                p.WaitForExit();

                lock (processLock)
                {
                    currentProcess = null;
                }

                if (!cancelled && p.ExitCode != 0)
                {
                    errorCount++;
                    SendToUI("ERROR: " + Path.GetFileName(exePath) + " exited with code " + p.ExitCode);
                }
            }
        }

        public void Close()
        {
            lock (trackLock)
            {
                if (overwrittenNames.Count > 0)
                {
                    SendToUI("\n\nWARNING: " + overwrittenNames.Count + " archive file(s) were overwritten by a later mod (the later mod wins):");
                    int shown = 0;
                    foreach (string n in overwrittenNames)
                    {
                        if (shown++ >= 50) break;
                        SendToUI("  " + n);
                    }
                    if (overwrittenNames.Count > 50)
                        SendToUI("  ... and " + (overwrittenNames.Count - 50) + " more");
                }
            }

            if (errorCount > 0)
            {
                SendToUI("\nWARNING: " + errorCount + " error(s) occurred - check the log above. The merged mod may be incomplete.");
                SendToUI("\n<---- Merge Complete (with errors) ---->");
            }
            else
            {
                SendToUI("\n<---- Merge Complete ---->");
            }
            Trace.WriteLine("merge thread finished");
        }

        private void SendToUI(string message)
        {
            UILogMessageEvent?.Invoke(this, new UILogMessageEventArgs { Message = message });
        }

        private void RecordFile(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return;

            lock (trackLock)
            {
                if (!files.Add(fileName))
                {
                    overwrittenNames.Add(fileName);
                    Trace.WriteLine("overwriting " + fileName);
                }
            }
        }

        private void TrackFile(string data)
        {
            if (data.Length < 5)
                return;

            // detect extraction, e.g. "(12/340) Extracting records/foo.dbr"
            if (data[0] == '(')
            {
                int close = data.IndexOf(')');
                if (close >= 0)
                {
                    int s = close + 2;
                    if (data.Contains("Extracting "))
                        s += 11;
                    if (s < data.Length)
                        RecordFile(data.Substring(s));
                }
            }

            // detect addition, e.g. "added: records/foo.dbr"
            int added = data.IndexOf("added: ", StringComparison.Ordinal);
            if (added >= 0)
                RecordFile(data.Substring(added + 7));
        }
    }
}
