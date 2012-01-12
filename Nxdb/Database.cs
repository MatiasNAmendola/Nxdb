﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml;
using Drac.Threading;
using org.basex.core;
using org.basex.core.cmd;
using org.basex.data;
using org.basex.query;
using org.basex.query.item;
using org.basex.query.iter;
using org.basex.query.up.primitives;
using org.basex.build;
using org.basex.util.list;
using Exception = System.Exception;
using String = System.String;

namespace Nxdb
{
    // Represents a single BaseX database into which all documents should be stored
    // Unlike the previous versions, it's recommended just one overall NxDatabase be used
    // and querying between them is not supported
    // The documents in the database can be grouped using paths in the document name,
    // i.e.: "folderA/docA.xml", "folderA/docB.xml", "folderB/docC.xml"
    public class Database : IDisposable, IEquatable<Database>, IQuery
    {
        #region Static

        private static readonly ReaderWriterLockSlim GlobalLockSlim = new ReaderWriterLockSlim();

        // The global locks also lock all databases individually, this helper class needed
        // to unlock all the database when the lock is disposed
        internal class LockWrapper : IDisposable
        {
            private readonly DisposableLock _globalLock;
            private readonly IDisposable[] _databaseLocks;

            public LockWrapper(DisposableLock globalLock, Func<Database, IDisposable> action)
            {
                _globalLock = globalLock;
                using (Databases.ReadLock())
                {
                    _databaseLocks = new IDisposable[Databases.Unsync.Count];
                    int c = 0;
                    foreach (Database database in Databases.Unsync.Values)
                    {
                        _databaseLocks[c++] = action(database);
                    }
                }
            }

            public void Dispose()
            {
                foreach(IDisposable databaseLock in _databaseLocks)
                {
                    databaseLock.Dispose();
                }
                _globalLock.Dispose();
            }
        }

        internal static LockWrapper GlobalWriteLock()
        {
            return new LockWrapper(new WriteLock(GlobalLockSlim), d => d.WriteLock());
        }

        internal static LockWrapper GlobalReadLock()
        {
            return new LockWrapper(new ReadLock(GlobalLockSlim), d => d.ReadLock());
        }

        internal static LockWrapper GlobalUpgradeableReadLock()
        {
            return new LockWrapper(new UpgradeableReadLock(GlobalLockSlim), d => d.UpgradeableReadLock());
        }

        //This runs some one-time initialization and needs to be called before use
        //Useful because several Java classes use reflection on their first construction
        //which hurts performance when the first construction is done during an operation
        //This method is not thread-safe, but that's okay because it should only be called once
        public static void Initialize(string path)
        {
            if (_context == null)
            {
                // Make sure we have a valid path
                if(String.IsNullOrEmpty(path))
                {
                    path = Path.Combine(Environment.CurrentDirectory, "Nxdb");
                }

                // Create the home path
                if (!Directory.Exists(path))
                {
                    Directory.CreateDirectory(path);
                }

                // Set the home path so the BaseX preference file will go there (rather than user path)
                Prop.HOME = Path.Combine(path, "pref");

                // Now we can create the context since the path for preferences has been set
                Context context = new Context();
                _properties = new NxdbProp();

                // Now set the database path
                context.mprop.set(MainProp.DBPATH, path);

                // Do some Java initialization by instantiating objects
                new QueryContext(context);
                new FElem(new QNm("init".Token()));

                // Set the context at the very end (to provide a little threading safety)
                _context = context;
            }
        }

        private static void Initialize()
        {
            Initialize(null);
        }

        //Use one static Context object - all opened databases will get added ("pinned") to the context
        //The primary Context.data() reference should remain null
        private static Context _context = null;

        internal static Context Context
        {
            get
            {
                if(_context == null)
                {
                    Initialize();
                }
                return _context;
            }
        }

        private static NxdbProp _properties = null;

        // Not thread-safe - need to surround with lock from caller
        internal static NxdbProp Properties
        {
            get
            {
                Initialize();    // Need to make sure we're initialized before getting the properties object
                return _properties;
            }
        }
        
        public static void Drop(string name)
        {
            if (name == null) throw new ArgumentNullException("name");
            if (name == String.Empty) throw new ArgumentException("name");

            using (GlobalWriteLock())
            {
                //Only drop if not currently pinned
                if (Context.pinned(name))
                {
                    throw new ArgumentException("Database is currently open, please dispose all references and try again.");
                }

                //Attempt to drop
                if (!DropDB.drop(name, Context.mprop))
                {
                    throw new Exception("Could not drop database.");
                }
            }
        }
        
        // Return value = opened (true = opened; false = created)
        public static bool Get(string name, out Database database)
        {
            if (name == null) throw new ArgumentNullException("name");
            if (name == String.Empty) throw new ArgumentException("name");

            //Try to open or create the database
            database = null;
            using (GlobalWriteLock())
            {
                try
                {
                    database = Get(Open.open(name, Context));
                    return true;
                }
                catch (Exception)
                {
                    try
                    {
                        database = Get(CreateDB.create(name, Parser.emptyParser(), Context));
                        return false;
                    }
                    catch (Exception ex)
                    {
                        throw new ArgumentException("Could not create database.", ex);
                    }
                }
            }
        }

        internal static Database Get(Data data)
        {
            if (data == null) throw new ArgumentNullException("data");
            Database database;
            using (Databases.WriteLock())
            {
                if (!Databases.Unsync.TryGetValue(data, out database))
                {
                    database = new Database(data);
                    Databases.Unsync.Add(data, database);
                }
            }
            return database;
        }

        public static Database Get(string name)
        {
            Database database;
            Get(name, out database);
            return database;
        }

        // A cache of all databases - the same instance should be returned for the same Data
        private static readonly ReadOnlySyncObject<Dictionary<Data, Database>> Databases 
            = new ReadOnlySyncObject<Dictionary<Data, Database>>(new Dictionary<Data, Database>()); 

        #endregion

        private readonly ReaderWriterLockSlim _lockSlim = new ReaderWriterLockSlim();

        internal ReaderWriterLockSlim LockSlim
        {
            get { return _lockSlim; }
        }

        internal WriteLock WriteLock()
        {
            return new WriteLock(_lockSlim);
        }

        internal ReadLock ReadLock()
        {
            return new ReadLock(_lockSlim);
        }

        internal UpgradeableReadLock UpgradeableReadLock()
        {
            return new UpgradeableReadLock(_lockSlim);
        }

        // A cache of all nodes for this database indexed by pre value
        private readonly SyncObject<WeakReference[]> _nodes
            = new SyncObject<WeakReference[]>(null);

        private Data _data;
        
        // Not thread-safe
        internal Data Data
        {
            get { return _data; }
        }

        private Database(Data data)
        {
            _data = data;
            _nodes = new SyncObject<WeakReference[]>(new WeakReference[data.meta.size]);
        }

        public void Dispose()
        {
            string dropName = Name; //This also checks for disposal
            using(GlobalWriteLock())
            {
                if(Context.unpin(_data))
                {
                    Databases.DoWrite(d => d.Remove(_data));
                    _data.close();
                    _data = null;
                    _nodes.DoWrite(n => _nodes.Unsync = null);
                }
            }
            if (dropName != null && Nxdb.Properties.DropOnDispose)
            {
                Drop(dropName);
            }
        }

        // Only called from the Node.Get() static methods - use those to get new nodes
        internal Node GetNode(int pre)
        {
            using (_nodes.ReadLock())
            {
                if (_nodes.Unsync == null) throw new ObjectDisposedException("Database");
                if (_nodes.Unsync[pre] != null)
                {
                    Node node = (Node) _nodes.Unsync[pre].Target;
                    if (node != null)
                    {
                        return node;
                    }
                }
                return null;
            }
        }

        internal void SetNode(int pre, Node node)
        {
            using (_nodes.WriteLock())
            {
                if (_nodes.Unsync == null) throw new ObjectDisposedException("Database");
                _nodes.Unsync[pre] = new WeakReference(node);
            }
        }

        // Called by Updates.Apply()
        internal void Update()
        {
            using (_nodes.WriteLock())
            {
                if (_nodes.Unsync == null) throw new ObjectDisposedException("Database");

                //Raise the Updated event
                EventHandler<EventArgs> handler = Updated;
                if (handler != null) handler(this, EventArgs.Empty);

                // Grow the nodes cache if needed (but never shrink it)
                if (_data.meta.size > _nodes.Unsync.Length)
                {
                    Array.Resize(ref _nodes.UnsyncField, _data.meta.size);
                }

                // Check validity and reposition nodes
                LinkedList<Node> reposition = new LinkedList<Node>();
                for (int c = 0; c < _nodes.Unsync.Length; c++)
                {
                    if (_nodes.Unsync[c] != null)
                    {
                        Node node = (Node)_nodes.Unsync[c].Target;
                        if (node != null)
                        {
                            if (!node.Validate())
                            {
                                // The node is now invalid, remove it from the cache
                                _nodes.Unsync[c] = null;
                            }
                            else
                            {
                                // The node is still valid, but if it moved add it to the reposition list
                                if (node.UnsyncIndex != c)
                                {
                                    reposition.AddLast(node);
                                    _nodes.Unsync[c] = null;
                                }
                            }
                        }
                        else
                        {
                            _nodes.Unsync[c] = null;
                        }
                    }
                }

                // Reposition any nodes that moved
                foreach (Node node in reposition)
                {
                    _nodes.Unsync[node.UnsyncIndex] = new WeakReference(node);
                }
            }
        }

        public event EventHandler<EventArgs> Updated;

        public string Name
        {
            get
            {
                using (ReadLock())
                {
                    if (_data == null) throw new ObjectDisposedException("Database");
                    return Data.meta.name;
                }
            }
        }

        /// <summary>
        /// Gets a timestamp representing the last database modification. This can be valuable
        /// when you need to check if the database has been modified.
        /// </summary>
        public long Time
        {
            get
            {
                using (ReadLock())
                {
                    if (_data == null) throw new ObjectDisposedException("Database");
                    return Data.meta.time;
                }
            }
        }

        /// <summary>
        /// Indicates whether the current database is equal to another database.
        /// </summary>
        /// <param name="other">A database to compare with this one.</param>
        /// <returns>
        /// true if the current database is equal to the <paramref name="other"/> parameter; otherwise, false.
        /// </returns>
        public bool Equals(Database other)
        {
            return other != null && this == other;
        }

        /// <summary>
        /// Determines whether the specified <see cref="object"/> is equal to this instance.
        /// </summary>
        /// <param name="obj">The <see cref="object"/> to compare with this instance.</param>
        /// <returns>
        ///   <c>true</c> if the specified <see cref="object"/> is equal to this instance; otherwise, <c>false</c>.
        /// </returns>
        /// <exception cref="T:System.NullReferenceException">
        /// The <paramref name="obj"/> parameter is null.
        ///   </exception>
        public override bool Equals(object obj)
        {
            Database other = obj as Database;
            return other != null && Equals(other);
        }

        /// <summary>
        /// Returns a hash code for this instance.
        /// </summary>
        /// <returns>
        /// A hash code for this instance, suitable for use in hashing algorithms and data structures like a hash table. 
        /// </returns>
        public override int GetHashCode()
        {
            using (ReadLock())
            {
                return Data == null ? 0 : Data.GetHashCode();
            }
        }
        
        //Many of the following are ported from FNDb.java to eliminate the overhead of the XQuery function evaluation

        public void Delete(string path)
        {   
            using (UpgradeableReadLock())
            {
                if (_data == null) throw new ObjectDisposedException("Database");
                IntList docs = Data.docs(path);
                using(new Updates())
                {
                    for (int i = 0, s = docs.size(); i < s; i++)
                    {
                        Updates.Add(new DeleteNode(docs.get(i), Data, null));
                    }
                }
            }
        }

        public void Rename(string path, string newName)
        {
            using (UpgradeableReadLock())
            {
                if (_data == null) throw new ObjectDisposedException("Database");
                IntList docs = Data.docs(path);
                using (new Updates())
                {
                    for (int i = 0, s = docs.size(); i < s; i++)
                    {
                        int pre = docs.get(i);
                        string target = org.basex.core.cmd.Rename.target(Data, pre, path, newName);
                        if (!String.IsNullOrEmpty(target))
                        {
                            Updates.Add(new ReplaceValue(pre, Data, null, target.Token()));
                        }
                    }
                }
            }
        }

        public void Optimize()
        {
            using (UpgradeableReadLock())
            {
                if (_data == null) throw new ObjectDisposedException("Database");
                Updates.Add(new DBOptimize(Data, Context, false, null));
            }
        }

        public void OptimizeAll()
        {
            using (UpgradeableReadLock())
            {
                if (_data == null) throw new ObjectDisposedException("Database");
                Updates.Add(new DBOptimize(Data, Context, true, null));
            }
        }
        
        public virtual void Add(string path, XmlReader xmlReader)
        {
            if (xmlReader == null) throw new ArgumentNullException("xmlReader");
            Add(path, Helper.GetNodeCache(xmlReader));
        }

        public virtual void Add(string path, string content)
        {
            Helper.CallWithString(content, path, Add);
        }

        public void Add(string path, params Document[] nodes)
        {
            if (nodes == null) throw new ArgumentNullException("nodes");
            Add(path, Helper.GetNodeCache(nodes));
        }

        public void Add(string path, IEnumerable<Document> nodes)
        {
            if (nodes == null) throw new ArgumentNullException("nodes");
            Add(path, Helper.GetNodeCache(nodes.Cast<Node>()));
        }

        private void Add(string path, NodeCache nodeCache)
        {
            using (UpgradeableReadLock())
            {
                if (_data == null) throw new ObjectDisposedException("Database");
                UnsyncAdd(path, nodeCache);
            }
        }

        // Not thread-safe, intended for use by thread-safe callers - also does not check for disposal
        private void UnsyncAdd(string path, NodeCache nodeCache)
        {
            if (nodeCache != null)
            {
                FDoc doc = new FDoc(nodeCache, path.Token());
                Updates.Add(new DBAdd(Data, null, doc, path, Context));
            }
        }

        public virtual void Replace(string path, XmlReader xmlReader)
        {
            if (xmlReader == null) throw new ArgumentNullException("xmlReader");
            Replace(path, Helper.GetNodeCache(xmlReader));
        }

        public virtual void Replace(string path, string content)
        {
            Helper.CallWithString(content, path, Replace);
        }

        public void Replace(string path, params Document[] nodes)
        {
            if (nodes == null) throw new ArgumentNullException("nodes");
            Replace(path, Helper.GetNodeCache(nodes));
        }

        public void Replace(string path, IEnumerable<Document> nodes)
        {
            if (nodes == null) throw new ArgumentNullException("nodes");
            Replace(path, Helper.GetNodeCache(nodes.Cast<Node>()));
        }

        private void Replace(string path, NodeCache nodeCache)
        {
            using (UpgradeableReadLock())
            {
                if (_data == null) throw new ObjectDisposedException("Database");
                using (new Updates())
                {
                    int pre = Data.doc(path);
                    if (pre != -1)
                    {
                        if (Data.docs(path).size() != 1) throw new ArgumentException("Simple document expected as replacement target");
                        Updates.Add(new DeleteNode(pre, Data, null));
                        UnsyncAdd(path, nodeCache);
                    }
                }
            }
        }
        
        public Document GetDocument(string name)
        {
            using (ReadLock())
            {
                if (_data == null) throw new ObjectDisposedException("Database");
                int pre = Data.doc(name);
                return pre == -1 ? null : (Document)Node.Get(pre, Data);    
            }
        }

        public IEnumerable<Document> GetDocuments(string path)
        {
            List<Document> documents = new List<Document>();
            using (ReadLock())
            {
                if (_data == null) throw new ObjectDisposedException("Database");
                IntList docs = Data.docs(path);
                for (int i = 0, s = docs.size(); i < s; i++)
                {
                    int pre = docs.get(i);
                    documents.Add((Document) Node.Get(pre, Data));
                }
            }
            return documents;
        }

        public IEnumerable<Document> Documents
        {
            get
            {
                List<Document> documents = new List<Document>();
                using (ReadLock())
                {
                    if (_data == null) throw new ObjectDisposedException("Database");
                    IntList il = Data.docs();
                    for (int c = 0; c < il.size(); c++)
                    {
                        int pre = il.get(c);
                        documents.Add((Document)Node.Get(pre, Data));
                    }
                }
                return documents;
            }
        }

        public IEnumerable<string> DocumentNames
        {
            get
            {
                List<string> names = new List<string>();
                using (ReadLock())
                {
                    if (_data == null) throw new ObjectDisposedException("Database");
                    IntList il = Data.docs();
                    for (int c = 0; c < il.size(); c++)
                    {
                        int pre = il.get(c);
                        names.Add(Data.text(pre, true).Token());
                    }
                }
                return names;
            }
        }

        public IEnumerable<object> Eval(string expression)
        {
            return new Query(this).Eval(expression);
        }

        public IEnumerable<T> Eval<T>(string expression)
        {
            return Eval(expression).OfType<T>();
        }

        public IList<object> EvalList(string expression)
        {
            return new List<object>(Eval(expression));
        }

        public IList<T> EvalList<T>(string expression)
        {
            return new List<T>(Eval(expression).OfType<T>());
        }

        public object EvalSingle(string expression)
        {
            return Eval(expression).FirstOrDefault();
        }

        public T EvalSingle<T>(string expression) where T : class
        {
            return EvalSingle(expression) as T;
        }
    }
}
