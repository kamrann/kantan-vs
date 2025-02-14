using Microsoft.VisualStudio.Extensibility.Editor;
using Microsoft.VisualStudio.Extensibility.Helpers;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Kantan
{
    using ConsumerId = uint;
    using SyncCounter = uint;

    [JsonDerivedType(typeof(DocumentOpen), typeDiscriminator: "open")]
    [JsonDerivedType(typeof(DocumentClose), typeDiscriminator: "close")]
    [JsonDerivedType(typeof(DocumentEdit), typeDiscriminator: "edit")]
    public abstract record DocumentEvent([property: JsonIgnore] SyncCounter sync);

    public record DocumentOpen(SyncCounter sync) : DocumentEvent(sync);
    public record DocumentClose(SyncCounter sync) : DocumentEvent(sync);
    public struct SimpleTextRange
    {
        public int start { get; init; } = 0;
        public int length { get; init; } = 0;

        public SimpleTextRange()
        { }

        public SimpleTextRange(int startIndex, int rangeLength)
        {
            start = startIndex;
            length = rangeLength;
        }

        public static SimpleTextRange Empty(int start)
        {
            return new SimpleTextRange(start, 0);
        }
    }
    public record TextModification(SimpleTextRange range, string text);
    public record DocumentEdit(SyncCounter sync, IEnumerable<TextModification> edits) : DocumentEvent(sync);

    internal interface IDocumentTrackingProvider
    {
        public void NotifyDocumentOpened(Uri uri, ITextDocumentSnapshot snapshot);

        public void NotifyDocumentClosed(Uri uri);

        public void NotifyDocumentUpdated(Uri uri, IReadOnlyList<TextEdit> edits, ITextDocumentSnapshot snapshot);
    }

    internal interface IDocumentTrackingConsumer
    {
        public ConsumerId RegisterConsumer(Action handler); // @todo: handler accepting delta updates
        public void UnregisterConsumer(ConsumerId consumerId);
        public IReadOnlyDictionary<Uri, IReadOnlyList<DocumentEvent>> ConsumeUpdates(ConsumerId consumerId); // @todo: delta updates, per document updates
    }

    // @todo: missing thread sync!

    internal class DocumentTrackingService : DisposableObject, IDocumentTrackingProvider, IDocumentTrackingConsumer
    {
        class TrackedDocument
        {
            List<DocumentEvent> _events = new();
            ITextDocumentSnapshot _latest;

            internal TrackedDocument(SyncCounter sync, ITextDocumentSnapshot latest)
            {
                _events.Add(new DocumentOpen(sync));
                // Initial single edit to represent delta from empty document to the current state.
                _events.Add(new DocumentEdit(sync, new [] {
                    new TextModification(SimpleTextRange.Empty(0), latest.Text.CopyToString()), // @TODO: perhaps more efficient to just store the TextRange reference, and then convert directly from that to the JSON to be sent.
                }));
                _latest = latest;
            }

            public void Update(SyncCounter sync, IReadOnlyList<TextEdit> edits, ITextDocumentSnapshot latest)
            {
                _events.Add(new DocumentEdit(sync, edits.Select(ed => new TextModification(new SimpleTextRange(ed.Range.Start.Offset, ed.Range.Length), ed.Text))));
                _latest = latest;
            }

            public void Close(SyncCounter sync)
            {
                _events.Add(new DocumentClose(sync));
            }

            public IReadOnlyList<DocumentEvent> GetEvents(SyncCounter consumerSync)
            {
                return _events
                    .SkipWhile(ev => consumerSync > ev.sync)
                    .ToImmutableList();
            }
        }

        private Dictionary<Uri, TrackedDocument> _documents = new();
        private ConsumerId _nextConsumerId = 0;
        class RegisteredConsumer
        {
            // The sync number corresponding to the first synchronization point that the consumer is yet to reach. In other words, one beyond the last sync point.
            // So 0 represents nothing yet synced.
            // Note this relates to the use of postfix increment on _syncCounter when setting document event sync numbers, and the fact we assign _syncCounter here after 
            // a consumer is synced.
            public SyncCounter sync = 0;
            public Action notify;

            public RegisteredConsumer(Action notifyHandler)
            {
                notify = notifyHandler;
            }
        }

        private Dictionary<ConsumerId, RegisteredConsumer> _consumers = new();
        private SyncCounter _syncCounter = 0;

        void IDocumentTrackingProvider.NotifyDocumentOpened(Uri uri, ITextDocumentSnapshot snapshot)
        {
            _documents.Add(uri, new TrackedDocument(_syncCounter++, snapshot));

            foreach (var (id, entry) in _consumers)
            {
                entry.notify.Invoke();
            }
        }

        void IDocumentTrackingProvider.NotifyDocumentClosed(Uri uri)
        {
            _documents[uri].Close(_syncCounter++);

            foreach (var (id, entry) in _consumers)
            {
                entry.notify.Invoke();
            }

            // @todo: will need to remove from the map when all have synced to the close point, so within ConsumeUpdates most likely.
            //_documents.Remove(uri);
        }

        void IDocumentTrackingProvider.NotifyDocumentUpdated(Uri uri, IReadOnlyList<TextEdit> edits, ITextDocumentSnapshot snapshot)
        {
            _documents[uri].Update(_syncCounter++, edits, snapshot);

            foreach (var (id, entry) in _consumers)
            {
                entry.notify.Invoke();
            }
        }

        uint IDocumentTrackingConsumer.RegisterConsumer(Action handler)
        {
            var id = _nextConsumerId++;
            _consumers[id] = new RegisteredConsumer(handler);
            handler.Invoke(); // For initial update
            return id;
        }

        void IDocumentTrackingConsumer.UnregisterConsumer(uint consumerId)
        {
            _consumers.Remove(consumerId);
        }

        IReadOnlyDictionary<Uri, IReadOnlyList<DocumentEvent>> IDocumentTrackingConsumer.ConsumeUpdates(uint consumerId)
        {
            var consumer = _consumers[consumerId];
            var result = _documents
                .Select(entry => KeyValuePair.Create(entry.Key, entry.Value.GetEvents(consumer.sync)))
                .Where(entry => entry.Value.Count > 0)
                .ToDictionary();
            consumer.sync = _syncCounter;
            return result;
        }
    }
}
