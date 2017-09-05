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
    /// <summary>
    /// Simple interface for interacting with the UCI engine
    /// </summary>
    public interface IUCIChessEngine
    {
        /// <summary>
        /// Fired when the engine has finished executing a command
        /// </summary>
        event EventHandler<UCIResponseReceivedEventArgs> OnUCICommandExecuted;

        /// <summary>
        /// Sends a command to the engine.
        /// </summary>
        /// <param name="commandString">UCI protocol string</param>
        /// <param name="expectedResponse">UCI protocol expected response, can be empty</param>
        void SendUCICommand(string commandString, string expectedResponse);

        /// <summary>
        /// Sends a quit command to the engine
        /// </summary>
        void Quit();
    }

    /// <summary>
    /// Response will always be a string.  This is pretty much the same as 
    /// DataReceivedEventArgs, but mine as I can't set the data on the other one
    /// </summary>
    public class UCIResponseReceivedEventArgs : EventArgs
    {
        /// <summary>
        /// Store the response string in a class
        /// </summary>
        /// <param name="data">the response from the UCI engine</param>
        public UCIResponseReceivedEventArgs(string data)
        {
            response = data;
        }

        /// <summary>
        /// stored response string
        /// </summary>
        private readonly string response;

        /// <summary>
        /// Property accessor for response string
        /// </summary>
        public string Response
        {
            get { return response; }
        }
    }

    /// <summary>
    /// Simple class to fire a command and wait on the response for the engine
    /// </summary>
    public class UCICommand : IUCIChessEngine, IDisposable
    {
        /// <summary>
        /// Delegate(s) listening to our event
        /// </summary>
        public event EventHandler<UCIResponseReceivedEventArgs> OnUCICommandExecuted;

        /// <summary>
        /// Internal event fired when a command is completed fully
        /// </summary>
        private AutoResetEvent readyToExecute = new AutoResetEvent(false);

        /// <summary>
        /// Cache the Process object
        /// </summary>
        /// <param name="p">Process object already created</param>
        public UCICommand(Process p)
        {
            process = p;
        }

        /// <summary>
        /// Cleanup
        /// </summary>
        void IDisposable.Dispose()
        {
            // removes our handler (really we could leave this up the entire app life)
            process.OutputDataReceived -= OnDataReceived;
        }

        /// <summary>
        /// Thread method for the actual write of the command
        /// </summary>
        /// <param name="sw">stdin StreamWriter for the process</param>
        public void ExecuteThreadProc(StreamWriter sw)
        {
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

        /// <summary>
        /// Event handler for process stdout.  This is where we parse responses
        /// </summary>
        /// <param name="sender">ignored</param>
        /// <param name="e">The string sent to stdout</param>
        public void OnDataReceived(object sender, DataReceivedEventArgs e)
        {
            // Write it out so we can see it for now as well
            if (PrintOutput)
            {
                Console.WriteLine(String.Concat("ERSP: ", e.Data));
            }
            else
            {
                // Show some progress though...
                Console.Write(".");
            }

            // compare e.Data to the expected string
            if (e.Data.StartsWith(expected))
            {
                if (!PrintOutput)
                {
                    Console.WriteLine(); // end progress line
                }
                
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

        /// <summary>
        /// Send a string to the engine
        /// </summary>
        /// <param name="commandString">command to sent</param>
        /// <param name="expectedResponse">response expected or empty for none</param>
        void IUCIChessEngine.SendUCICommand(string commandString, string expectedResponse)
        {
            Console.WriteLine(String.Concat("CMD : ", commandString));

            command = commandString;
            expected = expectedResponse;

            // Hook into the stdout redirected stream -assumes the process
            // has in fact redirected this stream for us
            process.OutputDataReceived += new DataReceivedEventHandler(OnDataReceived);

            syncAfterCommand = false; // Reset from prior use
            printOutput = true;
            if (expected.Length == 0)
            {
                // No response is given, so sync to "isready/readyok"
                syncAfterCommand = true;
            }

            if (commandString.StartsWith("go"))
            {
                printOutput = false; // don't draw the lines (testing)
            }

            Execute(process.StandardInput);
        }

        /// <summary>
        /// Send a quit command, do not bother waiting
        /// </summary>
        void IUCIChessEngine.Quit()
        {
            // someone should be waiting on the process quitting if they care
            process.StandardInput.WriteLine("quit");
            ((IDisposable)this).Dispose();
        }

        /// <summary>
        /// Spins up the thread to execute the command and waits on the response
        /// event *not* the thread to close
        /// </summary>
        /// <param name="sw">redirected stream for process stdin</param>
        /// <returns>wait result</returns>
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

        // Command field and Property
        private string command;
        public string Command
        {
            get { return command; }
        }

        // Expected field and Property
        private string expected;
        public string Expected
        {
            get { return expected; }
        }

        // Draw output field and Property
        private bool printOutput;
        public bool PrintOutput
        {
            get { return printOutput; }
        }


        private bool syncAfterCommand = false;
        private static string IsReady = "isready";
        private static string ReadyOk = "readyok";
        private readonly Process process;
    }

    /// <summary>
    /// Wraps the actual UCI engine process
    /// </summary>
    public class ProcessWrapper
    {
        public ProcessWrapper(string name)
        {
            processName = name;

            p = new Process();

            // Set process and startup variables
            p.EnableRaisingEvents = true;
            p.StartInfo.CreateNoWindow = true;
            p.StartInfo.RedirectStandardOutput = true;
            p.StartInfo.RedirectStandardInput = true;
            p.StartInfo.RedirectStandardError = true;
            p.StartInfo.UseShellExecute = false;
            p.StartInfo.FileName = processName;

            // If this fails we'll get an exception writing in a sec
            p.Start(); 

            // Create a command class
            ucicmd = new UCICommand(p);

            // Start async reading of the output stream
            p.BeginOutputReadLine();
        }

        /// <summary>
        /// Blocks until the wrapped process exits
        /// </summary>
        public void WaitForExit()
        {
            p.WaitForExit();
        }

        /// <summary>
        /// Property to access the command class
        /// </summary>
        public UCICommand Command
        {
            get { return ucicmd; }
        }

        private string processName;
        private Process p;
        private UCICommand ucicmd;
    }

    /// <summary>
    /// Main entry class for Console
    /// </summary>
    class Program
    {
        // Testing with full path, obviously hardcoded to my machine
        static private String ProcessName = @"D:\arena_3.5.1\Engines\stockfish-8-win\Windows\stockfish_8_x64.exe";

        private static string bestmove = "";

        /// <summary>
        /// Handler for the IUCIChessEngine event fired after commands are processed
        /// </summary>
        /// <param name="sender">ignored</param>
        /// <param name="e">holds the response string</param>
        static public void UCIResponseReceivedEventHandler(object sender, UCIResponseReceivedEventArgs e)
        {
            // Raised on completion of commands
            Console.WriteLine(String.Concat("IRSP: ", e.Response));

            // If we're asking for a move - then save the response we care about
            // the SAN for the move - it comes right after "bestmove"
            // If no move (e.g. mate) will return 'bestmove (none)'
            if (e.Response.StartsWith("bestmove"))
            {
                string[] parts = e.Response.Split(' ');
                bestmove = parts[1];
            }
        }

        /// <summary>
        /// Entry point to exe
        /// </summary>
        /// <param name="args"></param>
        static void Main(string[] args)
        {
            // Create a wrapper class (starts the process)
            ProcessWrapper pw = new ProcessWrapper(ProcessName);
            
            // Add an EventHandler for the event raised when commands are finished
            pw.Command.OnUCICommandExecuted += new EventHandler<UCIResponseReceivedEventArgs>(UCIResponseReceivedEventHandler);

            // Get the interface from our object
            IUCIChessEngine engine = pw.Command as IUCIChessEngine;

            // Use the engine interface to send some setup commands
            engine.SendUCICommand("isready", "readyok");
            engine.SendUCICommand("uci", "uciok");
            //engine.SendUCICommand("setoption name Skill Level value 0", "");
            engine.SendUCICommand("ucinewgame", "");
            engine.SendUCICommand("d", ""); // debug - stockfish will draw board and show FEN etc

            // Make some moves
            string movecommand = "position startpos moves ";
            
            // Let the engine do it all for testing
            // stop after 1000 halfmoves (500 moves) or when there is no move
            for (int count = 0; count < 1000; count++)
            {
                engine.SendUCICommand("go movetime 10000", "bestmove");
                if (String.Compare("(none)", bestmove) != 0)
                {
                    // each move concats the previous set of moves, as the engine will need it
                    // for the next evaluation.  We could also calculate our FEN each time and
                    // pass that, but this is far easier and faster
                    engine.SendUCICommand(movecommand = String.Concat(movecommand, " ", bestmove), "");
                }
                else
                {
                    // No more moves...could be mate or draw or resign(?)
                    Console.WriteLine("No more moves!");
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
