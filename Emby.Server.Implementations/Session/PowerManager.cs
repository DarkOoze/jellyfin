namespace Emby.Server.Implementations.Session
{
    using System;
    using System.Collections.Concurrent;
    using System.Diagnostics;
    using System.Diagnostics.CodeAnalysis;
    using System.Linq;
    using System.Runtime.InteropServices;
    using System.Threading;
    using System.Threading.Tasks;

    using MediaBrowser.Controller.Library;
    using MediaBrowser.Controller.Session;

    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;

    internal class PowerManager : BackgroundService, IDisposable
    {
        private readonly ILogger<PowerManager> _logger;
        private readonly PlaybackManager _playbackManager;

        public PowerManager(ISessionManager sessionManager, ILogger<PowerManager> logger)
        {
            _logger = logger;
            _playbackManager = new PlaybackManager(sessionManager);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!TryCreateSleepLockFactory(out var lockFactory))
            {
                return;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                if (!_playbackManager.PlaybackOccurs)
                {
                    await Task.Delay(1000, stoppingToken).ConfigureAwait(false);

                    continue;
                }

                using (lockFactory())
                {
                    await _playbackManager.PlaybackCompleted(stoppingToken).ConfigureAwait(false);
                }
            }
        }

        private static bool TryCreateSleepLockFactory([NotNullWhen(true)] out Func<IDisposable?>? factory)
        {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                factory = () => new Win32SleepLock();
                return true;
            }

            if (Environment.OSVersion.Platform == PlatformID.Unix && System.IO.File.Exists("systemd-inhibit"))
            {
                factory = () => new SystemdInhibitorProcess();
                return true;
            }

            factory = default;
            return false;
        }

        public override void Dispose()
        {
            base.Dispose();

            _playbackManager.Dispose();
        }

        private sealed class PlaybackManager : IDisposable
        {
            private readonly ISessionManager _sessionManager;
            private readonly ConcurrentDictionary<string, TaskCompletionSource> _currentPlaybackSessions;

            public PlaybackManager(ISessionManager sessionManager)
            {
                _sessionManager = sessionManager;
                _currentPlaybackSessions = new ConcurrentDictionary<string, TaskCompletionSource>();

                _sessionManager.PlaybackStart += SessionManagerOnPlaybackStart;
                _sessionManager.PlaybackStopped += SessionManagerOnPlaybackStopped;
            }

            public bool PlaybackOccurs => !_currentPlaybackSessions.IsEmpty;

            public async Task PlaybackCompleted(CancellationToken cancellationToken)
            {
                while (this.PlaybackOccurs)
                {
                    var tasks = _currentPlaybackSessions.Values.Select(tcs => tcs.Task).ToArray();

                    await WhenAllWithCancellation(tasks, cancellationToken);
                }
            }

            public void Dispose()
            {
                _sessionManager.PlaybackStart -= SessionManagerOnPlaybackStart;
                _sessionManager.PlaybackStopped -= SessionManagerOnPlaybackStopped;
            }

            private static async Task WhenAllWithCancellation(Task[] tasks, CancellationToken cancellationToken)
            {
                foreach (var task in tasks)
                {
                    await task.WaitAsync(cancellationToken);
                }
            }

            private void SessionManagerOnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
            {
                _currentPlaybackSessions.GetOrAdd(e.PlaySessionId, _ => new TaskCompletionSource());
            }

            private void SessionManagerOnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
            {
                if (_currentPlaybackSessions.TryRemove(e.PlaySessionId, out var taskCompletionSource))
                {
                    taskCompletionSource.TrySetResult();
                }
            }
        }

        private sealed class SystemdInhibitorProcess : IDisposable
        {
            private const string FileName = "systemd-inhibit";
            private const string Arguments = "--what=sleep --who=Jellyfin --why=\"Streaming media to user.\"";

            private readonly Process _process;

            public SystemdInhibitorProcess()
            {
                _process = new Process()
                {
                    StartInfo = new ProcessStartInfo(FileName, Arguments),
                };

                _process.Start();
            }

            public void Dispose()
            {
                try
                {
                    _process.Kill();
                    _process.Dispose();
                }
                catch (InvalidOperationException) // Process aldready terminated.
                {
                }
            }
        }

        private sealed class Win32SleepLock : IDisposable
        {
            private readonly Thread _thread;
            private readonly TaskCompletionSource _taskCompletionSource;

            public Win32SleepLock()
            {
                _taskCompletionSource = new TaskCompletionSource();
                _thread = new Thread(StartThread) { IsBackground = true };
                _thread.Start(_taskCompletionSource.Task);
            }

            [Flags]
            private enum ExecutionState : uint
            {
                /// <summary>
                /// Forces the system to be in the working state by resetting the system idle timer.
                /// </summary>
                SystemRequiered = 0x00000001,

                /// <summary>
                /// Forces the display to be on by resetting the display idle timer.
                /// </summary>
                DisplayRequiered = 0x00000002,

                /// <summary>
                /// Enables away mode. This value must be specified with <see cref="ExecutionState.Continous"/>.
                /// Away mode should be used only by media-recording and media-distribution applications that must perform critical background processing on desktop computers while the computer appears to be sleeping.
                /// </summary>
                AwayMode = 0x00000040,

                /// <summary>
                /// Informs the system that the state being set should remain in effect until the next call that uses <see cref="ExecutionState.Continous"/> and one of the other state flags is cleared.
                /// </summary>
                Continous = 0x80000000,
            }

            /// <summary>
            /// Enables an application to inform the system that it is in use, thereby preventing the system from entering sleep or turning off the display while the application is running.
            /// </summary>
            /// <param name="flags">The thread's execution requirements. <see href="https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-setthreadexecutionstate"/>.</param>
            /// <returns>If the function succeeds, the return value is the previous thread execution state. If the function fails, the return value is NULL.</returns>
            [DllImport("kernel32.dll")]
            [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
            private static extern uint SetThreadExecutionState(ExecutionState flags);

            private static void StartThread(object? obj)
            {
                var task = (Task)obj!;

                var mode = ExecutionState.SystemRequiered | ExecutionState.Continous;

                _ = SetThreadExecutionState(mode);

                task.Wait();

                _ = SetThreadExecutionState(ExecutionState.Continous);
            }

            public void Dispose()
            {
                _taskCompletionSource.SetResult();
                _thread.Join();
            }
        }
    }
}
