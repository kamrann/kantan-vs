using Microsoft.VisualStudio.RpcContracts.Commands;
using Microsoft.VisualStudio.Threading;
using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Kantan
{
    class DocumentPipeServer
    {
        private static readonly string PipeName = "kantan.document_tracker";

        private const int MaxInstances = 5;
        private const uint BufferSize = 512;
        private const char MessageDelimiter = '\n';

        private IDocumentTrackingConsumer _trackingService;
        private OutputUtilsService _outputService;

        public DocumentPipeServer(IDocumentTrackingConsumer trackingService, OutputUtilsService outputService)
        {
            _trackingService = trackingService;
            _outputService = outputService;
        }

        private static async Task<T?> ReadMessageAsync<T>(NamedPipeServerStream pipe, CancellationToken cancellationToken)
        {
            using var memoryStream = new MemoryStream();
            byte[] buffer = new byte[BufferSize];

            do
            {
                int bytesRead = await pipe.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                await memoryStream.WriteAsync(buffer, 0, bytesRead);
            }
            while (!pipe.IsMessageComplete);  // Ensure full message is received

            memoryStream.Seek(0, SeekOrigin.Begin);
            return await JsonSerializer.DeserializeAsync<T>(memoryStream, cancellationToken: cancellationToken);
        }

        //class ClientRequest

        public async Task InstanceThreadAsync(CancellationToken cancellationToken)
        {
            int threadId = Thread.CurrentThread.ManagedThreadId;

            while (!cancellationToken.IsCancellationRequested)
            {
                using NamedPipeServerStream pipeStream = new NamedPipeServerStream(PipeName, PipeDirection.InOut, MaxInstances, PipeTransmissionMode.Message, PipeOptions.Asynchronous);

                await _outputService.WriteToOutputWindowAsync(string.Format("Waiting for connection from clients on thread[{0}].", threadId), cancellationToken);

                // Wait for a client to connect
                try
                {
                    await pipeStream.WaitForConnectionAsync(cancellationToken);
                }
                catch (Exception e)
                {
                    await _outputService.WriteToOutputWindowAsync(string.Format("Problem waiting for client to connect to pipe: {0}", e.Message), cancellationToken);
                    break; // temp bail
                }

                await _outputService.WriteToOutputWindowAsync(string.Format("Client connected on thread[{0}].", threadId), cancellationToken);

                CancellationTokenSource disconnectTokenSource = new CancellationTokenSource();

                using Stream writeStream = Stream.Synchronized(pipeStream);

                // Start a monitoring task to detect disconnection
                Task monitorTask = Task.Run(async () =>
                {
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        await Task.Delay(500); // Poll every 500ms

                        // @NOTE: Attempt a write since apparently most robust way of detecting client disconnection.
                        try
                        {
                            // @NOTE: Just sending empty object to avoid needing to complicate the protocol.
                            byte[] jsonBytes = Encoding.UTF8.GetBytes("{}" + MessageDelimiter);
                            await writeStream.WriteAsync(jsonBytes, 0, jsonBytes.Length, cancellationToken: cancellationToken);
                        }
                        catch (IOException)
                        {
                            // Client disconnected
                            await _outputService.WriteToOutputWindowAsync(string.Format("Client disconnection detected."), cancellationToken);

                            pipeStream.Disconnect();
                            await disconnectTokenSource.CancelAsync(); // Signal the main loop
                            break;
                        }
                    }
                });

                AutoResetEvent documentUpdateEvent = new(false);

                var trackingId = _trackingService.RegisterConsumer(() => documentUpdateEvent.Set());

                // @todo: exit condition
                while (pipeStream.IsConnected)
                {
                    // @todo: if we also want to wait for messages from the client, add ReadMessageAsync to Task.WhenAny call.
                    await Task.WhenAny(
                        documentUpdateEvent.ToTask(cancellationToken: cancellationToken),
                        Task.Run(() => Task.Delay(-1, disconnectTokenSource.Token))
                        );
                    var updates = _trackingService.ConsumeUpdates(trackingId);
                    if (updates.Count > 0)
                    {
                        try
                        {
                            // Buffer JSON first so we can append the delimiter and ensure all are written atomically.
                            using var memoryStream = new MemoryStream();
                            await JsonSerializer.SerializeAsync(memoryStream, updates, cancellationToken: cancellationToken);
                            byte[] suffixBytes = Encoding.UTF8.GetBytes(new string(MessageDelimiter, 1));
                            await memoryStream.WriteAsync(suffixBytes, 0, suffixBytes.Length);

                            memoryStream.Seek(0, SeekOrigin.Begin);
                            await memoryStream.CopyToAsync(writeStream);
                        }
                        catch (IOException e)
                        {
                            pipeStream.Disconnect();
                        }
                    }
                }

                _trackingService.UnregisterConsumer(trackingId);

                await monitorTask;
            }

            await _outputService.WriteToOutputWindowAsync(string.Format("Connection closed on thread[{0}].", threadId), cancellationToken);
        }
    }
}
