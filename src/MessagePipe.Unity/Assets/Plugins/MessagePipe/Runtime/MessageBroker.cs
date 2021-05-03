using MessagePipe.Internal;
using System;
using System.Runtime.CompilerServices;

namespace MessagePipe
{
    public sealed class MessageBroker<TMessage> : IPublisher<TMessage>, ISubscriber<TMessage>
    {
        readonly MessageBrokerCore<TMessage> core;
        readonly FilterAttachedMessageHandlerFactory handlerFactory;

        public MessageBroker(MessageBrokerCore<TMessage> core, FilterAttachedMessageHandlerFactory handlerFactory)
        {
            this.core = core;
            this.handlerFactory = handlerFactory;
        }

        public void Publish(TMessage message)
        {
            core.Publish(message);
        }

        public IDisposable Subscribe(IMessageHandler<TMessage> handler, params MessageHandlerFilter<TMessage>[] filters)
        {
            return core.Subscribe(handlerFactory.CreateMessageHandler(handler, filters));
        }
    }

    public sealed class BufferedMessageBroker<TMessage> : IBufferedPublisher<TMessage>, IBufferedSubscriber<TMessage>
    {
        readonly BufferedMessageBrokerCore<TMessage> core;
        readonly FilterAttachedMessageHandlerFactory handlerFactory;

        public BufferedMessageBroker(BufferedMessageBrokerCore<TMessage> core, FilterAttachedMessageHandlerFactory handlerFactory)
        {
            this.core = core;
            this.handlerFactory = handlerFactory;
        }

        public void Publish(TMessage message)
        {
            core.Publish(message);
        }

        public IDisposable Subscribe(IMessageHandler<TMessage> handler, params MessageHandlerFilter<TMessage>[] filters)
        {
            return core.Subscribe(handlerFactory.CreateMessageHandler(handler, filters));
        }
    }

    public sealed class BufferedMessageBrokerCore<TMessage>
    {
        readonly MessageBrokerCore<TMessage> core;
        TMessage lastMessage;

        public BufferedMessageBrokerCore(MessageBrokerCore<TMessage> core)
        {
            this.core = core;
            this.lastMessage = default;
        }

        public void Publish(TMessage message)
        {
            lastMessage = message;
            core.Publish(message);
        }

        public IDisposable Subscribe(IMessageHandler<TMessage> handler, params MessageHandlerFilter<TMessage>[] filters)
        {
            if (lastMessage != null)
            {
                handler.Handle(lastMessage);
            }
            return core.Subscribe(handler);
        }
    }

    public sealed class MessageBrokerCore<TMessage> : IDisposable, IHandlerHolderMarker
    {
        readonly FreeList<IMessageHandler<TMessage>> handlers;
        readonly MessagePipeDiagnosticsInfo diagnotics;
        readonly HandlingSubscribeDisposedPolicy handlingSubscribeDisposedPolicy;
        readonly object gate = new object();
        bool isDisposed;

        public MessageBrokerCore(MessagePipeDiagnosticsInfo diagnotics, MessagePipeOptions options)
        {
            this.handlers = new FreeList<IMessageHandler<TMessage>>();
            this.handlingSubscribeDisposedPolicy = options.HandlingSubscribeDisposedPolicy;
            this.diagnotics = diagnotics;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Publish(TMessage message)
        {
            var array = handlers.GetValues();
            for (int i = 0; i < array.Length; i++)
            {
                array[i]?.Handle(message);
            }
        }

        public IDisposable Subscribe(IMessageHandler<TMessage> handler)
        {
            lock (gate)
            {
                if (isDisposed) return handlingSubscribeDisposedPolicy.Handle(nameof(MessageBrokerCore<TMessage>));

                var subscriptionKey = handlers.Add(handler);
                var subscription = new Subscription(this, subscriptionKey);
                diagnotics.IncrementSubscribe(this, subscription);
                return subscription;
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                // Dispose is called when scope is finished.
                if (!isDisposed && handlers.TryDispose(out var count))
                {
                    isDisposed = true;
                    diagnotics.RemoveTargetDiagnostics(this, count);
                }
            }
        }

        sealed class Subscription : IDisposable
        {
            bool isDisposed;
            readonly MessageBrokerCore<TMessage> core;
            readonly int subscriptionKey;

            public Subscription(MessageBrokerCore<TMessage> core, int subscriptionKey)
            {
                this.core = core;
                this.subscriptionKey = subscriptionKey;
            }

            public void Dispose()
            {
                if (!isDisposed)
                {
                    isDisposed = true;
                    lock (core.gate)
                    {
                        if (!core.isDisposed)
                        {
                            core.handlers.Remove(subscriptionKey, true);
                            core.diagnotics.DecrementSubscribe(core, this);
                        }
                    }
                }
            }
        }
    }
}