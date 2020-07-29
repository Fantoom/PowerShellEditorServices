﻿using Microsoft.Extensions.Logging;
using Microsoft.PowerShell.EditorServices.Services.PowerShell.Console;
using Microsoft.PowerShell.EditorServices.Services.PowerShell.Execution;
using Microsoft.PowerShell.EditorServices.Services.PowerShell.Host;
using Microsoft.PowerShell.EditorServices.Services.PowerShell.Utility;
using Microsoft.PowerShell.EditorServices.Utility;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.PowerShell.EditorServices.Services.PowerShell
{
    internal class PowerShellConsoleService : IDisposable
    {
        public static PowerShellConsoleService CreateAndStart(
            ILoggerFactory loggerFactory,
            PowerShellExecutionService executionService)
        {
            return new PowerShellConsoleService(
                loggerFactory,
                executionService,
                executionService.EditorServicesHost,
                executionService.ReadLine,
                executionService.PSReadLineProxy);
        }

        private readonly ILogger _logger;

        private readonly PowerShellExecutionService _executionService;

        private readonly EditorServicesConsolePSHost _editorServicesHost;

        private readonly ConsoleReadLine _readLine;

        private readonly PSReadLineProxy _psrlProxy;

        private readonly ConcurrentStack<ReplTask> _replLoopTaskStack;

        // This is required because PSRL will keep prompting for keys as we push a new REPL task
        // Keeping current command cancellations on their own stack simplifies access to the cancellation token
        // for the REPL command that's currently running.
        private readonly ConcurrentStack<CommandCancellation> _commandCancellationStack;

        private ConsoleKeyInfo? _lastKey;

        private bool _exiting;

        private bool _debugging;

        private PowerShellConsoleService(
            ILoggerFactory loggerFactory,
            PowerShellExecutionService executionService,
            EditorServicesConsolePSHost editorServicesHost,
            ConsoleReadLine readLine,
            PSReadLineProxy psrlProxy)
        {
            _logger = loggerFactory.CreateLogger<PowerShellConsoleService>();
            _replLoopTaskStack = new ConcurrentStack<ReplTask>();
            _commandCancellationStack = new ConcurrentStack<CommandCancellation>();
            _executionService = executionService;
            _editorServicesHost = editorServicesHost;
            _readLine = readLine;
            _psrlProxy = psrlProxy;
            _exiting = false;
            _debugging = false;
        }

        public void StartRepl()
        {
            System.Console.CancelKeyPress += OnCancelKeyPress;
            System.Console.InputEncoding = Encoding.UTF8;
            System.Console.OutputEncoding = Encoding.UTF8;
            _psrlProxy.OverrideReadKey(ReadKey);
            _executionService.PowerShellPushed += OnPowerShellPushed;
            _executionService.PromptCancellationRequested += OnPromptCancellationRequested;
            _executionService.FrameExiting += OnFrameExiting;
            PushNewReplTask();
        }

        public void Dispose()
        {
            while (_replLoopTaskStack.Count > 0)
            {
                StopCurrentRepl();
            }

            System.Console.CancelKeyPress -= OnCancelKeyPress;
            _executionService.PowerShellPushed -= OnPowerShellPushed;
            _executionService.PromptCancellationRequested -= OnPromptCancellationRequested;
            _executionService.FrameExiting -= OnFrameExiting;
        }

        public void CancelCurrentPrompt()
        {
            if (_commandCancellationStack.TryPeek(out CommandCancellation commandCancellation))
            {
                commandCancellation.CancellationSource?.Cancel();
            }
        }

        public void StopCurrentRepl()
        {
            if (_replLoopTaskStack.TryPeek(out ReplTask currentReplTask))
            {
                currentReplTask.ReplCancellationSource.Cancel();
            }
        }

        private async Task RunReplLoopAsync()
        {
            _replLoopTaskStack.TryPeek(out ReplTask replTask);

            try
            {
                while (!replTask.ReplCancellationSource.IsCancellationRequested)
                {
                    var currentCommandCancellation = new CommandCancellation();
                    _commandCancellationStack.Push(currentCommandCancellation);

                    try
                    {
                        string promptString = await GetPromptStringAsync(currentCommandCancellation.CancellationSource.Token).ConfigureAwait(false);

                        if (currentCommandCancellation.CancellationSource.IsCancellationRequested)
                        {
                            continue;
                        }

                        WritePrompt(promptString);

                        string userInput = await InvokeReadLineAsync(currentCommandCancellation.CancellationSource.Token).ConfigureAwait(false);

                        // If the user input was empty it's because:
                        //  - the user provided no input
                        //  - the readline task was canceled
                        //  - CtrlC was sent to readline (which does not propagate a cancellation)
                        //
                        // In any event there's nothing to run in PowerShell, so we just loop back to the prompt again.
                        // However, we must distinguish the last two scenarios, since PSRL will not print a new line in those cases.
                        if (string.IsNullOrEmpty(userInput))
                        {
                            if (currentCommandCancellation.CancellationSource.IsCancellationRequested
                                || LastKeyWasCtrlC())
                            {
                                _editorServicesHost.UI.WriteLine();
                            }
                            continue;
                        }

                        await InvokeInputAsync(userInput, currentCommandCancellation.CancellationSource.Token).ConfigureAwait(false);

                        if (replTask.ReplCancellationSource.IsCancellationRequested)
                        {
                            break;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        continue;
                    }
                    catch (Exception e)
                    {
                        // TODO: Do something here
                    }
                    finally
                    {
                        _commandCancellationStack.TryPop(out CommandCancellation _);
                        currentCommandCancellation.CancellationSource.Dispose();
                        currentCommandCancellation.CancellationSource = null;
                    }
                }
            }
            finally
            {
                _exiting = false;
                _replLoopTaskStack.TryPop(out _);
                replTask.ReplCancellationSource.Dispose();
            }

        }

        private async Task<string> GetPromptStringAsync(CancellationToken cancellationToken)
        {
            string prompt = (await GetPromptOutputAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault() ?? "PS> ";

            if (_editorServicesHost.Runspace.RunspaceIsRemote)
            {
                prompt = _editorServicesHost.Runspace.GetRemotePrompt(prompt);
            }

            return prompt;
        }

        private Task<IReadOnlyList<string>> GetPromptOutputAsync(CancellationToken cancellationToken)
        {
            var promptCommand = new PSCommand().AddCommand("prompt");

            return _executionService.ExecutePSCommandAsync<string>(
                promptCommand,
                new PowerShellExecutionOptions(),
                cancellationToken);
        }

        private void WritePrompt(string promptString)
        {
            _editorServicesHost.UI.Write(promptString);
        }

        private Task<string> InvokeReadLineAsync(CancellationToken cancellationToken)
        {
            return _readLine.ReadCommandLineAsync(cancellationToken);
        }

        private Task InvokeInputAsync(string input, CancellationToken cancellationToken)
        {
            var command = new PSCommand().AddScript(input);
            var executionOptions = new PowerShellExecutionOptions
            {
                WriteOutputToHost = true,
                AddToHistory = true,
            };

            return _executionService.ExecutePSCommandAsync(command, executionOptions, cancellationToken);
        }

        private void OnCancelKeyPress(object sender, ConsoleCancelEventArgs args)
        {
            CancelCurrentPrompt();
        }

        private void OnPowerShellPushed(object sender, PowerShellPushedArgs args)
        {
            if ((args.FrameType & PowerShellFrameType.NonInteractive) == 0)
            {
                PushNewReplTask();
            }
        }

        private void OnPromptCancellationRequested(object sender, PromptCancellationRequestedArgs args)
        {
            CancelCurrentPrompt();
        }

        private void OnFrameExiting(object sender, FrameExitingArgs args)
        {
            _exiting = true;
            StopCurrentRepl();
        }

        private void OnReplCanceled()
        {
            // Ordinarily, when the REPL is canceled
            // we want to propagate the cancellation to any currently running command.
            // However, when the REPL is canceled by an 'exit' command,
            // the currently running command is doing the cancellation.
            // Not only would canceling it not make sense
            // but trying to cancel it from its own thread will deadlock PowerShell.
            // Instead we just let the command progress.

            if (_exiting)
            {
                return;
            }

            CancelCurrentPrompt();
        }

        private ConsoleKeyInfo ReadKey(bool intercept)
        {
            _commandCancellationStack.TryPeek(out CommandCancellation commandCancellation);

            // PSRL doesn't tell us when CtrlC was sent.
            // So instead we keep track of the last key here.
            // This isn't functionally required,
            // but helps us determine when the prompt needs a newline added

            _lastKey = ConsoleProxy.SafeReadKey(intercept, commandCancellation.CancellationSource.Token);
            return _lastKey.Value;
        }

        private bool LastKeyWasCtrlC()
        {
            return _lastKey != null
                && _lastKey.Value.Key == ConsoleKey.C
                && (_lastKey.Value.Modifiers & ConsoleModifiers.Control) != 0
                && (_lastKey.Value.Modifiers & ConsoleModifiers.Alt) == 0;
        }

        private void PushNewReplTask()
        {
            ReplTask.PushAndStart(_replLoopTaskStack, RunReplLoopAsync, OnReplCanceled);
        }


        private class ReplTask
        {
            public static void PushAndStart(
                ConcurrentStack<ReplTask> replLoopTaskStack,
                Func<Task> replLoopTaskFunc,
                Action cancellationAction)
            {
                var replLoopCancellationSource = new CancellationTokenSource();
                replLoopCancellationSource.Token.Register(cancellationAction);
                var replTask = new ReplTask(replLoopCancellationSource);
                replLoopTaskStack.Push(replTask);
                replTask.LoopTask = Task.Run(replLoopTaskFunc, replLoopCancellationSource.Token);
            }

            public ReplTask(CancellationTokenSource cancellationTokenSource)
            {
                ReplCancellationSource = cancellationTokenSource;
                Guid = Guid.NewGuid();
            }

            public Task LoopTask { get; private set;  }

            public CancellationTokenSource ReplCancellationSource { get; }

            public Guid Guid { get; }
        }

        private class CommandCancellation
        {
            public CommandCancellation()
            {
                CancellationSource = new CancellationTokenSource();
            }

            public CancellationTokenSource CancellationSource { get; set; }
        }
    }
}
