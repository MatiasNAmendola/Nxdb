﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using ikvm.@internal;
using java.io;
using Nxdb.Io;
using java.lang;
using java.math;
using javax.xml.datatype;
using javax.xml.@namespace;
using org.basex.api.xmldb;
using org.basex.core;
using org.basex.core.cmd;
using org.basex.data;
using org.basex.io;
using org.basex.query.item;
using org.basex.query.iter;
using org.basex.util;
using org.basex.build;
using org.basex.util.list;
using org.xmldb.api;
using Exception = System.Exception;
using File = java.io.File;
using String = System.String;
using StringReader = java.io.StringReader;
using Type = org.basex.query.item.Type;

namespace Nxdb
{
    // Represents a single BaseX database into which all documents should be stored
    // Unlike the previous versions, it's recommended just one overall NxDatabase be used
    // and querying between them is not supported
    // The documents in the database can be grouped using paths in the document name,
    // i.e.: "folderA/docA.xml", "folderA/docB.xml", "folderB/docC.xml"
    public class NxDatabase : IDisposable
    {
        private static string _home = null;

        public static void SetHome(string path)
        {
            if (path == null) throw new ArgumentNullException("path");
            if (path == String.Empty) throw new ArgumentException("path");

            _home = path;

            // Create the home path
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }

            // Set the home path so the BaseX preference file will go there (rather than user path)
            Prop.HOME = Path.Combine(_home, "pref");
        }

        public static bool Drop(string name)
        {
            if (name == null) throw new ArgumentNullException("name");
            if (name == String.Empty) throw new ArgumentException("name");

            return Run(new DropDB(name), GetContext());
        }

        private static Context GetContext()
        {
            // Set a default home if one hasn't been provided yet
            if (_home == null)
            {
                SetHome(Path.Combine(Environment.CurrentDirectory, "Nxdb"));
            }

            // Now we can create the context since the path for preferences has been set
            Context context = new Context();

            // Now set the database path
            context.mprop.set(MainProp.DBPATH, _home);

            return context;
        }

        private readonly Context _context;

        public NxDatabase(string name)
        {
            if (name == null) throw new ArgumentNullException("name");
            if (name == String.Empty) throw new ArgumentException("name");
            
            _context = GetContext();

            // Open/create the requested database
            if(!Run(new Open(name)))
            {
                // Unable to open it, try creating
                if (!Run(new CreateDB(name)))
                {
                    throw new ArgumentException();
                }
            }
        }

        public void Dispose()
        {
            Run(new Close());
        }

        // BaseX commands can be run in one of two ways:
        // 1) An instance of the command class can be created and passed to the following Run() method
        // 2) When the above won't work, a Func or Action can be used to wrap a command and run it

        internal static bool Run(Command command, Context context)
        {
            bool ret = command.run(context);
            string info = command.info();
            Debug.WriteLine(info);  //TODO: Replace this with some kind of logging mechanism
            return ret;
        }

        internal static bool Run(Func<Context, string> func, Context context)
        {
            return Run(new FuncCommand(func), context);
        }

        internal static bool Run(Action<Context> action, Context context)
        {
            return Run(new FuncCommand(action), context);
        }

        internal bool Run(Command command)
        {
            return Run(command, _context);
        }

        internal bool Run(Func<Context, string> func)
        {
            return Run(new FuncCommand(func));
        }

        internal bool Run(Action<Context> action)
        {
            return Run(new FuncCommand(action));
        }

        // The following perform the same operation as their equivalent BaseX command:
        // http://docs.basex.org/wiki/Commands
        
        public bool Add(string name, string content)
        {
            //TODO: Use the streaming version that takes an XmlReader - it appears to be faster
            return Run(new Add(name, content));
        }

        public bool Delete(string name)
        {
            return Run(new Delete(name));
        }

        public bool Rename(string name, string newName)
        {
            return Run(new Rename(name, newName));
        }

        public bool Replace(string name, string content)
        {
            // TODO: Use the streaming version that takes an XmlReader - it appears to be faster
            return Run(new Replace(name, content));
        }

        public bool Optimize()
        {
            return Run(new Optimize());
        }

        public bool OptimizeAll()
        {
            return Run(new OptimizeAll());
        }

        // More direct access

        public void Add(string name, XmlReader reader)
        {
            if (name == null) throw new ArgumentNullException("name");
            if (name == String.Empty) throw new ArgumentException("name");
            if (reader == null) throw new ArgumentNullException("reader");
            using (new UpdateContext())
            {
                Data data = GetData(name, reader);
                if(data != null)
                {
                    Data.insert(Data.meta.size, -1, data);
                    Context.update();
                    Data.flush();
                    //TODO: Drop the temp database if using DiskData (but how to know here?!)
                    data.close();
                }
            }
        }

        internal Context Context
        {
            get { return _context; }
        }

        internal Data Data
        {
            get { return _context.data(); }
        }

        public NxNode GetDocument(string name)
        {
            int pre = Data.doc(name);
            return pre == -1 ? null : new NxNode(this, pre);
        }

        public IEnumerable<NxNode> Documents
        {
            get
            {
                IntList il = Data.docs();
                for(int c = 0 ; c < il.size() ; c++ )
                {
                    int pre = il.get(c);
                    yield return new NxNode(this, pre);
                }
            }
        }

        public IEnumerable<string> DocumentNames
        {
            get
            {
                IntList il = Data.docs();
                for (int c = 0; c < il.size(); c++)
                {
                    int pre = il.get(c);
                    yield return Data.text(pre, true).Token();
                }
            }
        }

        internal int GetId(int pre)
        {
            return Data.id(pre);
        }

        internal int GetPre(int id)
        {
            return Data.pre(id);
        }

        internal long GetTime()
        {
            return Data.meta.time;
        }

        internal int GetSize()
        {
            return Data.meta.size;
        }

        // This is used to get an atomic type object or a node object from a database item
        internal object GetObjectForItem(Item item)
        {
            // Check for a null item
            if (item == null) return null;

            // Is it a node?
            ANode node = item as ANode;
            if (node != null)
            {
                return new NxNode(this, node);
            }
            
            // Get the Java object
            object obj = item.toJava();

            // Clean up non-.NET values
            if(obj is BigInteger)
            {
                BigInteger bigInteger = (BigInteger) obj;
                obj = Convert.ToDecimal(bigInteger.toString());
            }
            else if(obj is BigDecimal)
            {
                BigDecimal bigDecimal = (BigDecimal) obj;
                obj = Convert.ToDecimal(bigDecimal.toString());
            }
            else if (obj is XMLGregorianCalendar)
            {
                XMLGregorianCalendar date = (XMLGregorianCalendar) obj;
                date.normalize();   // Normalizes the date to UTC
                obj = XmlConvert.ToDateTime(date.toXMLFormat(), XmlDateTimeSerializationMode.Utc);
            }
            else if(obj is Duration)
            {
                Duration duration = (Duration) obj;
                obj = XmlConvert.ToTimeSpan(duration.toString());
            }
            else if(obj is QName)
            {
                QName qname = (QName) obj;
                obj = new XmlQualifiedName(qname.getLocalPart(), qname.getNamespaceURI());
            }

            return obj;
        }

        // Gets a Data instance for a given content stream
        internal Data GetData(string name, string content)
        {
            using (TextReader text = new System.IO.StringReader(content))
            {
                using (XmlReader reader = XmlReader.Create(text))
                {
                    return GetData(name, reader);
                }
            }
        }

        internal Data GetData(string name, XmlReader reader)
        {
            if (name == null) throw new ArgumentNullException("name");
            if (name == String.Empty) throw new ArgumentException("name");
            if (reader == null) throw new ArgumentNullException("reader");
            Builder builder = new MemBuilder(name, new XmlReaderParser(name, reader), Context.prop);
            // TODO: Use DiskData for larger documents (but how to tell if a stream is going to be large?!)
            Data data = null;
            try
            {
                data = builder.build();
                if (data.meta.size > 1)
                {
                    builder.close();
                    return data;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);  // TODO: Replace this with some kind of logging mechanism
            }
            finally
            {
                try { builder.close(); } catch { }
                if (data != null) try { data.close(); } catch { }
                // TODO: Drop the temp database if using DiskData
            }
            return null;
        }

        // A cache of all constructed DOM nodes for this collection
        // Needed because .NET XML DOM consumers probably expect one object per node instead of the on the fly creation that Nxdb uses
        // This ensures reference equality for equivalent NxNodes
        // Key = node Id, Value = WeakReference to XmlNode instance
        private readonly Dictionary<int, WeakReference> _domCache = new Dictionary<int, WeakReference>();

        internal Dictionary<int, WeakReference> DomCache
        {
            get { return _domCache; }
        }
    }
}
