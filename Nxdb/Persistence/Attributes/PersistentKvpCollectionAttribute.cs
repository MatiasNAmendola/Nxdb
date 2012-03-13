﻿/*
 * Copyright 2012 WildCard, LLC
 * 
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 * 
 * http://www.apache.org/licenses/LICENSE-2.0
 * 
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 * 
 */

using System;
using Nxdb.Node;

namespace Nxdb.Persistence.Attributes
{
    /// <summary>
    /// Stores or fetches a collection of KeyValuePair objects to/from a child element of the
    /// container element. This supports dictionaries, sorted lists, etc.
    /// If more than one element with the given name exists, the first one will be used.
    /// By default this stores and fetches data in the following format:
    /// <code>
    /// <Container>
    ///   <Name>
    ///     <Pair><Key>...</Key><Value>...</Value></Pair>
    ///     <Pair><Key>...</Key><Value>...</Value></Pair>
    ///     <Pair><Key>...</Key><Value>...</Value></Pair>
    ///   </Name>
    /// </Container>
    /// </code> 
    /// There are many parameters that can be used to control the formatting such as changing the
    /// element names of key or value elements, storing keys or values as attributes or evaluating
    /// queries to fetch keys or values.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    public class PersistentKvpCollectionAttribute : PersistentMemberAttribute
    {
        /// <summary>
        /// Gets or sets the element name to use or create. If unspecified, the name of
        /// the field or property will be used (as converted to a valid XML name).
        /// This is exclusive with Query and both may not be specified.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Gets or sets the element name to use or create for the key/value container. If unspecified, "Pair" will be used.
        ///  This is exclusive with PairQuery and both may not be specified.
        /// </summary>
        public string PairName { get; set; }

        /// <summary>
        /// Gets or sets the query to use for getting a pair. This is evaluated from the context of the parent
        /// element. If a pair query is used, the dictionary is read only and will not be persisted to the database.
        /// </summary>
        public string PairQuery { get; set; }

        /// <summary>
        /// Gets or sets the element name to use or create for key items. If unspecified, "Key" will be used. This is exclusive
        /// with KeyAttributeName and KeyQuery and only one may be specified.
        /// </summary>
        public string KeyElementName { get; set; }

        /// <summary>
        /// Gets or sets the attribute name to use or create for key items. This is exclusive
        /// with KeyElementName and KeyQuery and only one may be specified.
        /// </summary>
        public string KeyAttributeName { get; set; }

        /// <summary>
        /// Gets or sets the query to use for getting a key. This is evaluated from the context of the parent
        /// pair element. If a key query is used, the dictionary is read only and will not be persisted to the database.
        /// This is exclusive with KeyAttributeName and KeyElementName and only one may be specified.
        /// </summary>
        public string KeyQuery { get; set; }

        /// <summary>
        /// Gets or sets the type of keys. If unspecified, the actual type of the keys will be used. If specified,
        /// this must be assignable to the type of the key in the dictionary.
        /// </summary>
        public Type KeyType { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the keys are persistent object types.
        /// If this is true, KeyAttributeName must not be specified and KeyQuery if
        /// specified should return an element node.
        /// </summary>
        public bool KeysArePersistentObjects { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether keys should be attached.
        /// Only meaningful if KeysArePersistentObjects is true.
        /// If true (default), the manager cache will be searched and an
        /// existing instance used or a new instance created and attached for each key. If false, a new
        /// detached instance will be created on every fetch for each key.
        /// </summary>
        public bool AttachKeys { get; set; }
        
        /// <summary>
        /// Gets or sets the element name to use or create for value items. If unspecified, "Value" will be used. This is exclusive
        /// with ValueAttributeName and ValueQuery and only one may be specified.
        /// </summary>
        public string ValueElementName { get; set; }

        /// <summary>
        /// Gets or sets the attribute name to use or create for value items. This is exclusive
        /// with ValueElementName and ValueQuery and only one may be specified.
        /// </summary>
        public string ValueAttributeName { get; set; }

        /// <summary>
        /// Gets or sets the query to use for getting a value. This is evaluated from the context of the parent
        /// pair element. If a value query is used, the dictionary is read only and will not be persisted to the database.
        /// This is exclusive with ValueAttributeName and ValueElementName and only one may be specified.
        /// </summary>
        public string ValueQuery { get; set; }

        /// <summary>
        /// Gets or sets the type of values. If unspecified, the actual type of the values will be used. If specified,
        /// this must be assignable to the type of the value in the dictionary.
        /// </summary>
        public Type ValueType { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the values are persistent object types.
        /// If this is true, ValueAttributeName must not be specified and ValueQuery if
        /// specified should return an element node.
        /// </summary>
        public bool ValuesArePersistentObjects { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether values should be attached.
        /// Only meaningful if ValuesArePersistentObjects is true.
        /// If true (default), the manager cache will be searched and an
        /// existing instance used or a new instance created and attached for each value. If false, a new
        /// detached instance will be created on every fetch for each value.
        /// </summary>
        public bool AttachValues { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="PersistentKvpCollectionAttribute"/> class.
        /// </summary>
        public PersistentKvpCollectionAttribute()
        {
            AttachKeys = true;
            AttachValues = true;
        }

        internal override void Inititalize(System.Reflection.MemberInfo memberInfo)
        {
            base.Inititalize(memberInfo);

            Name = GetName(Name, memberInfo.Name, Query, CreateQuery);
            PairName = GetName(PairName, "Pair", PairQuery);
            KeyAttributeName = GetName(KeyAttributeName, null, KeyElementName, KeyQuery);
            KeyElementName = GetName(KeyElementName, "Key", KeyAttributeName, KeyQuery);
            ValueAttributeName = GetName(ValueAttributeName, null, ValueElementName, ValueQuery);
            ValueElementName = GetName(ValueElementName, "Value", ValueAttributeName, ValueQuery);

            if(KeysArePersistentObjects && !String.IsNullOrEmpty(KeyAttributeName))
                throw new Exception("KeyAttributeName must not be specified if KeysArePersistentObjects is true.");
            if (ValuesArePersistentObjects && !String.IsNullOrEmpty(ValueAttributeName))
                throw new Exception("ValueAttributeName must not be specified if ValuesArePersistentObjects is true.");
        }

        internal override object FetchValue(Element element, object target, TypeCache typeCache, Cache cache)
        {
            throw new NotImplementedException();
        }

        internal override object SerializeValue(object source, TypeCache typeCache, Cache cache)
        {
            if (!String.IsNullOrEmpty(PairQuery)
                || !String.IsNullOrEmpty(KeyQuery)
                || !String.IsNullOrEmpty(ValueQuery))
                return null;

            // Key/key = key source object, key/value = serialized key data
            // Value/key = value source object, value/value = serialized value data

            throw new NotImplementedException();
        }

        internal override void StoreValue(Element element, object serialized, object source, TypeCache typeCache, Cache cache)
        {
            throw new NotImplementedException();
        }
    }
}