using System;
using System.Threading;
using Fleck;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Text;
namespace Ooui
{
    public class FlickWebSocketSession : Session
    {
        readonly IWebSocketConnection webSocket;
        readonly Action<Message> handleElementMessageSent;

        readonly CancellationTokenSource sessionCts = new CancellationTokenSource ();
        readonly CancellationTokenSource linkedCts;
        readonly CancellationToken token;

        readonly System.Timers.Timer sendThrottle;
        DateTime lastTransmitTime = DateTime.MinValue;
        readonly TimeSpan throttleInterval = TimeSpan.FromSeconds (1.0 / UI.MaxFps);

        public FlickWebSocketSession (IWebSocketConnection webSocket, Element element, bool disposeElementAfterSession, double initialWidth, double initialHeight, Action<string, Exception> errorLogger, CancellationToken serverToken)
            : base (element, disposeElementAfterSession, initialWidth, initialHeight, errorLogger)
        {
            this.webSocket = webSocket;

            //
            // Create a new session cancellation token that will trigger
            // automatically if the server shutsdown or the session shutsdown.
            //
            linkedCts = CancellationTokenSource.CreateLinkedTokenSource (serverToken, sessionCts.Token);
            token = linkedCts.Token;

            //
            // Preparse handlers for the element
            //
            handleElementMessageSent = QueueMessage;

            //
            // Create a timer to use as a throttle when sending messages
            //
            sendThrottle = new System.Timers.Timer (throttleInterval.TotalMilliseconds);
            sendThrottle.Elapsed += (s, e) => {
                // System.Console.WriteLine ("TICK SEND THROTTLE FOR {0}", element);
                if ((e.SignalTime - lastTransmitTime) >= throttleInterval) {
                    sendThrottle.Enabled = false;
                    lastTransmitTime = e.SignalTime;
                    TransmitQueuedMessages ();
                }
            };
        }
        TaskCompletionSource<bool> runningTcs = new TaskCompletionSource<bool> ();
        public async Task RunAsync ()
        {
            //
            // Start watching for changes in the element
            //
            element.MessageSent += handleElementMessageSent;

            try {
                //
                // Add it to the document body
                //
                if (element.WantsFullScreen) {
                    element.Style.Width = initialWidth;
                    element.Style.Height = initialHeight;
                }
                QueueMessage (Message.Call ("document.body", "appendChild", element));

                webSocket.OnClose = () => {
                    sessionCts.Cancel ();
                    runningTcs.TrySetResult (false);
                };
                webSocket.OnError = (e) => {
                    sessionCts.Cancel ();
                    runningTcs.TrySetException (e);
                };
                webSocket.OnMessage = (m) => {
                    try {
                        var message = Newtonsoft.Json.JsonConvert.DeserializeObject<Message> (m);
                        element.Receive (message);
                    }
                    catch (Exception ex) {
                        Error ("Failed to process received message", ex);
                    }
                };

                await runningTcs.Task;              
            }
            finally {
                element.MessageSent -= handleElementMessageSent;

                if (disposeElementAfterSession && (element is IDisposable disposable)) {
                    try {
                        disposable.Dispose ();
                    }
                    catch (Exception ex) {
                        Error ("Failed to dispose of element", ex);
                    }
                }
            }
        }

        protected override void QueueMessage (Message message)
        {
            base.QueueMessage (message);
            sendThrottle.Enabled = true;
        }

        async void TransmitQueuedMessages ()
        {
            try {
                //
                // Dequeue as many messages as we can
                //
                var messagesToSend = new List<Message> ();
                System.Runtime.CompilerServices.ConfiguredTaskAwaitable task;
                lock (queuedMessages) {
                    messagesToSend.AddRange (queuedMessages);
                    queuedMessages.Clear ();

                    if (messagesToSend.Count == 0)
                        return;

                    //
                    // Now actually send this message
                    // Do this while locked to make sure SendAsync is called in the right order
                    //
                    task = Task.Run (() => {
                        var json = Newtonsoft.Json.JsonConvert.SerializeObject (messagesToSend);
                        webSocket.Send (json);
                    }).ConfigureAwait (false);
                }
                await task;
            }
            catch (Exception ex) {
                Error ("Failed to send queued messages, aborting session", ex);
                element.MessageSent -= handleElementMessageSent;
                sessionCts.Cancel ();
            }
        }
    }
}
