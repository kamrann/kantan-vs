using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

namespace Kantan
{
    class DocumentPipeServer
    {
        private static readonly string PipeName = "kantan.document_tracker";

        private const int MaxInstances = 1;
        private const uint BufferSize = 512;
        private const byte MessageDelimiter = (byte)'\n';

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
            using NamedPipeServerStream pipeServer = new NamedPipeServerStream(PipeName, PipeDirection.InOut, MaxInstances, PipeTransmissionMode.Message, PipeOptions.Asynchronous);

            int threadId = Thread.CurrentThread.ManagedThreadId;

            // Wait for a client to connect
            await pipeServer.WaitForConnectionAsync(cancellationToken);

            await _outputService.WriteToOutputWindowAsync(string.Format("Client connected on thread[{0}].", threadId), cancellationToken);

            AutoResetEvent documentUpdateEvent = new(false);

            var trackingId = _trackingService.RegisterConsumer(() => documentUpdateEvent.Set());

            // @todo: exit condition
            while (pipeServer.IsConnected)
            {
                // @todo: if we also want to wait for messages from the client, use Task.WhenAny along with ReadMessageAsync
                bool updatesPending = await documentUpdateEvent.ToTask(cancellationToken: cancellationToken);
                if (updatesPending)
                {
                    var updates = _trackingService.ConsumeUpdates(trackingId);
                    // Serialize JSON directly to the pipe.
                    await JsonSerializer.SerializeAsync(pipeServer, updates, cancellationToken: cancellationToken);
                    pipeServer.WriteByte(MessageDelimiter);

                    // try / catch (IOException e) needed?
                }
            }

            _trackingService.UnregisterConsumer(trackingId);

            await _outputService.WriteToOutputWindowAsync(string.Format("Connection closed on thread[{0}].", threadId), cancellationToken);
        }
    }
}
