using System;
using System.Threading;
using System.Diagnostics;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ConsoleAppProcessWrapProto
{
    // simple interface for interacting with the UCI engine
    public interface IUCIChessEngine
    {
        // When fired, this returns the response from the engine if any was given
        event EventHandler<UCIResponseReceivedEventArgs> OnUCICommandExecuted;

        void SendUCICommand(string commandString, string expectedResponse);
        void Quit();
    }

    // Response will always be a string.  This is pretty much the same as 
    // DataReceivedEventArgs, but mine
    public class UCIResponseReceivedEventArgs : EventArgs
    {
        public UCIResponseReceivedEventArgs(string data)
        {
            // Save the string
            response = data;
        }

        // Property for response string
        private readonly string response;
        public string Response
        {
            get { return response; }
        }
    }

    // Simple class to fire a command and await on the response for the engine
    public class UCICommand : IUCIChessEngine, IDisposable
    {
        public event EventHandler<UCIResponseReceivedEventArgs> OnUCICommandExecuted;

        // Fired when a command is completed fully
        private AutoResetEvent readyToExecute = new AutoResetEvent(false);

        public UCICommand(Process p)
        {
            process = p; // cache the process object
        }

        void IDisposable.Dispose()
        {
            // removes our handler (really we could leave this up the entire app life)
            process.OutputDataReceived -= OnDataReceived;
        }

        // Thread method for the actual write of the command(s)
        public void ExecuteThreadProc(StreamWriter sw)
        {
            // Write to process's redirected stdin
            sw.WriteLine(command);

            // if SyncAfterCommand == true, then also send IsReady and set 
            // expected to ReadyOk
            if (syncAfterCommand)
            {
                command = IsReady;
                expected = ReadyOk;
                sw.WriteLine(command);
            }

            // Done, exit thread - wait is elsewhere
        }

        // Event handler for process stdout.  This is where we parse responses
        public void OnDataReceived(object sender, DataReceivedEventArgs e)
        {
            // Write it out so we can see it for now
            Console.WriteLine(e.Data);

            // compare e.Data to the expected string
            if (e.Data.StartsWith(expected))
            {
                // raise interface event here if there is a handler
                if (OnUCICommandExecuted != null)
                {
                    OnUCICommandExecuted(this, new UCIResponseReceivedEventArgs(e.Data));

                    // Stop listening until next command / started in SendUCICommand
                    // again this could be left up
                    process.OutputDataReceived -= OnDataReceived;
                }

                // Signal event that we're done processing this command
                readyToExecute.Set();
            }
        }

        // Send a string to the engine
        void IUCIChessEngine.SendUCICommand(string commandString, string expectedResponse)
        {
            Console.WriteLine(commandString);

            command = commandString;
            expected = expectedResponse;

            // Hook into the stdout redirected stream -assumes the process
            // has in fact redirected this stream for us
            process.OutputDataReceived += new DataReceivedEventHandler(OnDataReceived);

            syncAfterCommand = false; // Reset from prior use
            if (expected.Length == 0)
            {
                // No response is given, so sync to "isready/readyok"
                syncAfterCommand = true;
            }

            Execute(process.StandardInput);
        }

        void IUCIChessEngine.Quit()
        {
            // someone should be waiting on the process quitting if they care
            process.StandardInput.WriteLine("quit");
            ((IDisposable)this).Dispose();
        }

        // Spins up the thread to execute the command and waits on the response
        // event *not* the thread
        public bool Execute(StreamWriter sw)
        {
            // Spin up a thread to do the work
            Thread thread = new Thread(() => ExecuteThreadProc(sw));
            thread.Start();

            // better to wait at the caller level in the real app and let this 
            // thread return since it's probably the UI thread, or a worker that
            // could do other work while waiting on the engine
            return readyToExecute.WaitOne();
        }

        private string command;
        public string Command
        {
            get { return command; }
        }

        private string expected;
        public string Expected
        {
            get { return expected; }
        }

        private bool syncAfterCommand = false;
        private static string IsReady = "isready";
        private static string ReadyOk = "readyok";
        private readonly Process process;
    }

    // Wraps the actual UCI engine process
    public class ProcessWrapper
    {
        public ProcessWrapper(string name)
        {
            processName = name;

            p = new Process();
            p.EnableRaisingEvents = true;
            p.StartInfo.CreateNoWindow = true;
            p.StartInfo.RedirectStandardOutput = true;
            p.StartInfo.RedirectStandardInput = true;
            p.StartInfo.RedirectStandardError = true;
            p.StartInfo.UseShellExecute = false;
            p.StartInfo.FileName = processName;

            p.Start(); // If this fails we'll get an exception writing in a sec

            ucicmd = new UCICommand(p);

            // Start async read
            p.BeginOutputReadLine();
        }

        public void WaitForExit()
        {
            p.WaitForExit();
        }

        public UCICommand Command
        {
            get { return ucicmd; }
        }

        private string processName;
        private Process p;
        private UCICommand ucicmd;

    }

    class Program
    {
        // Testing with full path, obviously hardcoded to my machine
        static private String ProcessName = @"D:\arena_3.5.1\Engines\stockfish-8-win\Windows\stockfish_8_x64.exe";

        private static string bestmove = "";
        static public void UCIResponseReceivedEventHandler(object sender, UCIResponseReceivedEventArgs e)
        {
            // Raised on completion of commands

            // If we're asking for a move - then save the response we care about
            // the SAN for the move - it comes right after "bestmove"
            // If no move (e.g. mate) will return 'bestmove (none)'
            if (e.Response.StartsWith("bestmove"))
            {
                string[] parts = e.Response.Split(' ');
                bestmove = parts[1];
            }
        }

        static void Main(string[] args)
        {
            ProcessWrapper pw = new ProcessWrapper(ProcessName);
            
            // Add an EventHandler for the event raised when commands are finished
            pw.Command.OnUCICommandExecuted += new EventHandler<UCIResponseReceivedEventArgs>(UCIResponseReceivedEventHandler);

            IUCIChessEngine engine = pw.Command as IUCIChessEngine;
            engine.SendUCICommand("isready", "readyok");
            engine.SendUCICommand("uci", "uciok");
            engine.SendUCICommand("setoption name Skill Level value 0", "");
            engine.SendUCICommand("ucinewgame", "");
            engine.SendUCICommand("d", ""); // debug - stockfish will draw board and show FEN etc

            // Make some moves
            string movecommand = "position startpos moves ";
            //engine.SendUCICommand(movecommand = String.Concat(movecommand, "e2e4"), "");

            // Let the engine do it all for testing
            // stop after 100 halfmoves (50 moves) or when there is no move
            for (int count = 0; count < 100; count++)
            {
                engine.SendUCICommand("go movetime 500", "bestmove");
                if (String.Compare("(none)", bestmove) != 0)
                {
                    engine.SendUCICommand(movecommand = String.Concat(movecommand, " ", bestmove), "");
                }
                else
                {
                    break;
                }
                engine.SendUCICommand("d", ""); // debug - stockfish will draw board and show FEN etc
            }

            // exits the engine process
            engine.Quit();

            // engine doesn't report "game over" or anthing like that, the caller must figure this
            // out, it just evaulates positions
            pw.WaitForExit();
        }
    }
}
