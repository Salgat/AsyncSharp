using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AsyncSharp
{
    public class MessageBus
    {
        private sealed class Subscriber
        {
            private readonly ConcurrentBag<Subscription> Subscriptions = new ConcurrentBag<Subscription>();
            
            public Subscriber() { }
            
            public Subscriber(Subscription subscription)
            {
                AddSubscription(subscription);
            }

            public void AddSubscription(Subscription subscription)
            {
                Subscriptions.Add(subscription);
            }

            public async Task HandleMessageAsync<TMessage>(TMessage message)
            {
                await Task.WhenAll(Subscriptions
                    .Where(s => InheritsOrImplementsCached(s.MessageType, typeof(TMessage)))
                    .Select(async subscription =>
                        {
                            try
                            {
                                subscription.ActionCallback?.Invoke(message);
                                if (subscription.AsyncCallback != null) await subscription.AsyncCallback(message).ConfigureAwait(false);
                            }
                            catch { }
                        })).ConfigureAwait(false);
            }
        }

        private sealed class Subscription
        {
            public Type MessageType;
            public Action<object> ActionCallback;
            public Func<object, Task> AsyncCallback;
        }
    
        private readonly static ConcurrentDictionary<(Type, Type), bool> _implementsOrInheretsCache = new ConcurrentDictionary<(Type, Type), bool>();
        private readonly ConcurrentDictionary<object, Subscriber> _subscriberById = new ConcurrentDictionary<object, Subscriber>();
        
        public void Push<TTag, TMessage>(TTag tag, TMessage message)
        {
            Task.Run(async () =>
            {
                await Task.WhenAll(_subscriberById.Select(subscriber => subscriber.Value.HandleMessageAsync(message))).ConfigureAwait(false);
            });
        }

        public void Subscribe<TTag, TMessage>(object id, TTag tag, Action<TMessage> callback)
        {
            var subscription = new Subscription()
            {
                MessageType = typeof(TMessage),
                ActionCallback = msg => callback((TMessage)msg)
            };
            _subscriberById.AddOrUpdate(id, new Subscriber(), (key, value) =>
            {
                value.AddSubscription(subscription);
                return value;
            });
        }

        public void Subscribe<TTag, TMessage>(object id, TTag tag, Func<TMessage, Task> callback)
        {
            var subscription = new Subscription()
            {
                MessageType = typeof(TMessage),
                AsyncCallback = async msg => await callback((TMessage)msg).ConfigureAwait(false)
            };
            _subscriberById.AddOrUpdate(id, new Subscriber(), (key, value) =>
            {
                value.AddSubscription(subscription);
                return value;
            });
        }

        public bool Unsubscribe(object id)
            => _subscriberById.TryRemove(id, out var _);

        #region InheritsOrImplements
        //https://stackoverflow.com/questions/457676/check-if-a-class-is-derived-from-a-generic-class
        public static bool InheritsOrImplementsCached(Type child, Type parent)
        {
            if (_implementsOrInheretsCache.TryGetValue((child, parent), out var result)) return result;
            return _implementsOrInheretsCache[(child, parent)] = InheritsOrImplements(child, parent);
        }

        public static bool InheritsOrImplements(Type child, Type parent)
        {

            parent = ResolveGenericTypeDefinition(parent);

            var currentChild = child.IsGenericType
                                   ? child.GetGenericTypeDefinition()
                                   : child;

            while (currentChild != typeof(object))
            {
                if (parent == currentChild || HasAnyInterfaces(parent, currentChild))
                    return true;

                currentChild = currentChild.BaseType != null
                               && currentChild.BaseType.IsGenericType
                                   ? currentChild.BaseType.GetGenericTypeDefinition()
                                   : currentChild.BaseType;

                if (currentChild == null)
                    return false;
            }
            return false;
        }

        private static bool HasAnyInterfaces(Type parent, Type child)
        {
            return child.GetInterfaces()
                .Any(childInterface =>
                {
                    var currentInterface = childInterface.IsGenericType
                        ? childInterface.GetGenericTypeDefinition()
                        : childInterface;

                    return currentInterface == parent;
                });
        }

        private static Type ResolveGenericTypeDefinition(Type parent)
        {
            var shouldUseGenericType = true;
            if (parent.IsGenericType && parent.GetGenericTypeDefinition() != parent)
                shouldUseGenericType = false;

            if (parent.IsGenericType && shouldUseGenericType)
                parent = parent.GetGenericTypeDefinition();
            return parent;
        }
        #endregion
    }
}
