﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Threading;
using DotNetSimaticDatabaseProtokollerLibrary.Common;
using DotNetSimaticDatabaseProtokollerLibrary.Databases.Interfaces;
using DotNetSimaticDatabaseProtokollerLibrary.SettingsClasses.Datasets;
using DotNetSimaticDatabaseProtokollerLibrary.SettingsClasses.Storage;
using MySql.Data;
using MySql.Data.MySqlClient;

namespace DotNetSimaticDatabaseProtokollerLibrary.Databases.MySQL
{
    public class MySQLStorage : IDBInterface, IDBViewable, IDBViewableSQL
    {
        private MySQLConfig myConfig;
        private IEnumerable<DatasetConfigRow> fieldList;
        private string dataTable;
        private string insertCommand = "";

        private DbConnection myDBConn;
        private DbCommand myCmd = new MySqlCommand();
        private DbDataReader myReader;
        
        
        public void Close()
        {
            if (myThread != null)
                myThread.Abort();
            if (myDBConn != null)
                myDBConn.Close();
        }

        public event ThreadExceptionEventHandler ThreadExceptionOccured;

        private string ConnectionString
        {
            get { return string.Format("server=" + myConfig.Server + ";user id=" + myConfig.Username + "; password=" + myConfig.Password + "; port=" + myConfig.Port + ";"); }
        }

        public void Connect_To_Database(StorageConfig config)
        {
            myConfig = config as MySQLConfig;
            if (myConfig == null)
                throw new Exception("Database Config is NULL");
            try
            {
                myDBConn = new MySqlConnection(ConnectionString);
                myDBConn.Open();
                if (myDBConn.State != System.Data.ConnectionState.Open)
                    throw new Exception("Unable to Open Database. Storage:" + config.Name);               
            }
            catch (Exception ex)
            {
                throw ex;
            }
        }

        public void CreateOrModify_TablesAndFields(string dataTable, DatasetConfig datasetConfig)
        {
            this.dataTable = dataTable;
            this.fieldList = datasetConfig.DatasetConfigRows;

            string sql = "";
            try
            {
                sql = "CREATE DATABASE " + myConfig.Database + ";";
                myCmd.Connection = myDBConn;
                myCmd.CommandText = sql;
                myCmd.ExecuteNonQuery();
            }
            catch (MySqlException ex)
            {
                if (ex.Number != 1007)
                {
                    Logging.LogText("Database could not be created. Storage: " + myConfig.Name + " Error:" + ex.Message, Logging.LogLevel.Error);
                    throw ex;
                }
            }

            myDBConn.ChangeDatabase(myConfig.Database);

            //Look if Table exists, when not, create it!
            try
            {
                sql = "SELECT * FROM " + dataTable + ";";
                myCmd.Connection = myDBConn;
                myCmd.CommandText = sql;
                myReader = myCmd.ExecuteReader();

            }
            catch (MySqlException ex)
            {
                if (ex.Number == 1146)
                {
                    try
                    {
                        sql = "CREATE TABLE " + dataTable + " (";
                        sql += "id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY); ";
                                                
                        myCmd.CommandText = sql;
                        myCmd.ExecuteNonQuery();

                        sql = "SELECT * FROM " + dataTable + ";";                        
                        myCmd.CommandText = sql;
                        myReader = myCmd.ExecuteReader();
                    }
                    catch (Exception ex_ex)
                    {
                        Logging.LogText("Database-table could not be created. Storage: " + myConfig.Name + ", Table: " + dataTable + ", Error:" + ex.Message, Logging.LogLevel.Error);
                        throw ex_ex;
                    }
                }
                else
                {
                    Logging.LogText("Error accessing Table. Storage: " + myConfig.Name + " Error:" + ex.Message, Logging.LogLevel.Error);
                    throw ex;
                }
            }            
            
            //Look for the Fields, create or alter them!
            List<String> existDBFelderliste = new List<string>();

            for (int n = 0; n < myReader.FieldCount; n++)
            {
                existDBFelderliste.Add(myReader.GetName(n));
            }
            myReader.Close();

            foreach (DatasetConfigRow myFeld in fieldList)
            {
                foreach (string existMyFeld in existDBFelderliste)
                {
                    if (myFeld.DatabaseField.ToLower() == existMyFeld.ToLower())
                    {
                        goto nextFeld;
                    }
                }

                //Feld existiert nicht -> erzeugen
                string dbfieldtype = myFeld.DatabaseFieldType;

                switch (dbfieldtype)
                {
                    case "bigint":
                        dbfieldtype = "bigint NOT NULL default 0";
                        break;
                    case "real":
                        dbfieldtype = "real NOT NULL default 0";
                        break;
                    case "datetime":
                        dbfieldtype = "TIMESTAMP NOT NULL";
                        break;
                    case "varchar":
                        dbfieldtype = "VARCHAR(" + myFeld.DatabaseFieldSize + ") NOT NULL DEFAULT ''";
                        break;
                }

                sql = "ALTER TABLE " + dataTable + " ADD COLUMN " + myFeld.DatabaseField + " " + dbfieldtype;

                try
                {
                    myCmd.Connection = myDBConn;
                    myCmd.CommandText = sql;
                    myCmd.ExecuteNonQuery();

                }
                catch (Exception ex)
                {
                    throw ex;
                }

                nextFeld:
                //Irgendeine anweisung, da sonst der Sprung nicht geht...
                {
                }
            }


            //Create Insert Command
            string wertliste = "", felderliste = "";
            foreach (DatasetConfigRow myFeld in fieldList)
            {
                if (wertliste != "")
                {
                    wertliste += ",";
                    felderliste += ",";
                }

                felderliste += myFeld.DatabaseField;
                wertliste += "?" + myFeld.DatabaseField;
            }
            insertCommand = "INSERT INTO " + dataTable + "(" + felderliste + ") values(" + wertliste + ")";
        }



        private Thread myThread;

        private List<IEnumerable<object>> _intValueList = new List<IEnumerable<Object>>();
        private int _maxAdd = 0;


        /// <summary>
        /// The write is added to a List and then put into an extra Thread, so that the PLC gets it's quitt imidiatly
        /// </summary>
        /// <param name="values"></param>
        public void Write(IEnumerable<object> values)
        {
            lock (_intValueList)
                _intValueList.Add(values);

            if (myThread == null)
            {
                myThread = new Thread(new ThreadStart(ThreadProc));
                myThread.Name = "Thread from Storage: " + myConfig.Name + " for Table: " + dataTable;
                myThread.Start();
            }
        }

        private void ThreadProc()
        {
            try
            {
                while (true)
                {


                    if (_intValueList.Count > 0)
                    {
                        lock (_intValueList)
                            _maxAdd = _intValueList.Count;
                        
                        try
                        {                           
                            _internal_Write();                            
                        }
                        catch (ThreadAbortException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            if (ThreadExceptionOccured != null)
                                ThreadExceptionOccured.Invoke(this, new ThreadExceptionEventArgs(ex));
                            else
                                Logging.LogText(ex.Message, Logging.LogLevel.Error);
                        }

                        _intValueList.RemoveRange(0, _maxAdd);
                    }
                    else
                        Thread.Sleep(20);
                }
            }
            catch (ThreadAbortException)
            {
                return;
            }
        }



        public bool _internal_Write()
        {
            //Look if the Connection is still open..
            try
            {
                string sql = "SELECT id FROM " + dataTable + " WHERE id = 0";
                myCmd.Connection = myDBConn;
                myCmd.CommandText = sql;
                myCmd.ExecuteNonQuery();
            }

            catch (Exception)
            {
                myDBConn.Close(); //Verbindung schließen!
                if (myDBConn.State != System.Data.ConnectionState.Open)
                {
                    myDBConn.Open();
                    if (myDBConn.State != System.Data.ConnectionState.Open)
                    {
                        return false;
                    }
                    myDBConn.ChangeDatabase(myConfig.Database);
                }
                else
                {
                    return false;
                }
            }

            //Add the Fields to the Database
            myCmd.Connection = myDBConn;
            myCmd.CommandText = insertCommand;                                

            
            for (int n = 0; n < _maxAdd; n++)
            //foreach (IEnumerable<object> values in _intValueList)
            {
                myCmd.Parameters.Clear();

                IEnumerable<object>  values = _intValueList[n];


                using (IEnumerator<DatasetConfigRow> e1 = fieldList.GetEnumerator())
                using (IEnumerator<object> e2 = values.GetEnumerator())
                {
                    while (e1.MoveNext() && e2.MoveNext())
                    {
                        //foreach (DatasetConfigRow field in fieldList)
                        //{
                        DatasetConfigRow field = e1.Current;
                        Object value = e2.Current; //values[fnr++];

                        myCmd.Parameters.Add(new MySqlParameter("?" + field.DatabaseField, value));                        
                    }
                }
               
                int tryCounter = 0;

                nomol:
                try
                {
                    myCmd.ExecuteNonQuery();                    
                }
                catch (System.Data.SQLite.SQLiteException ex)
                {
                    if (ex.ErrorCode == System.Data.SQLite.SQLiteErrorCode.Locked || ex.ErrorCode == System.Data.SQLite.SQLiteErrorCode.Busy)
                    {
                        tryCounter++;
                        if (tryCounter > 20)
                            throw new Exception("Datenbank nach 20 Versuchen immer noch locked!!");
                        goto nomol;
                    }
                }
                catch (Exception ex)
                {
                    throw ex;
                }
            }

            /*
            //Ringpufferarchiv...
            if (myProtokollDaten.MaxDatasets > 0)
            {
                string delstr = "DELETE FROM " + myProtokollDaten.DataBaseTable + " WHERE id <= (SELECT max(id) FROM " + myProtokollDaten.DataBaseTable + ") - (" + myProtokollDaten.MaxDatasets.ToString() + ")";
                myCmd.CommandText = delstr;
                myCmd.ExecuteNonQuery();
            }
            */
            return true;
        
        }

        public void Dispose()
        {
            if (myThread != null)
                myThread.Abort();
            if (myDBConn != null)
                myDBConn.Dispose();
            if (readDBConn != null)
                readDBConn.Dispose();
        }

        #region IDBViewable
        private DbConnection readDBConn;
        private DbCommand readCmd = new MySqlCommand();

        private void CheckAndEstablishReadConnection()
        {
            if (readDBConn == null)
            {
                readDBConn = new MySqlConnection(ConnectionString);
                readDBConn.Open();
                readDBConn.ChangeDatabase(myConfig.Database);
            } 
        }
        public DataTable ReadData(DatasetConfig datasetConfig, long Start, int Count)
        {
            try
            {
                CheckAndEstablishReadConnection();

                readCmd.Connection = readDBConn;
                readCmd.CommandText = "SELECT * FROM " + datasetConfig.Name + " LIMIT " + Count.ToString() + " OFFSET " + Start.ToString();
                DbDataReader akReader = readCmd.ExecuteReader();

                DataTable myTbl = new DataTable();
                myTbl.Load(akReader);
                akReader.Close();

                return myTbl;
            }
            catch (Exception ex)
            { }
            return null;
        }

        public DataTable ReadData(DatasetConfig datasetConfig, string sql, int Count)
        {
            try
            {
                CheckAndEstablishReadConnection();

                readCmd.Connection = readDBConn;
                readCmd.CommandText = sql.Trim();
                if (readCmd.CommandText.EndsWith(";"))
                    readCmd.CommandText = readCmd.CommandText.Substring(0, readCmd.CommandText.Length - 1);
                if (!readCmd.CommandText.Contains("LIMIT") && !readCmd.CommandText.Contains("OFFSET"))
                    readCmd.CommandText += " LIMIT " + Count.ToString();
                DbDataReader akReader = readCmd.ExecuteReader();

                DataTable myTbl = new DataTable();
                myTbl.Load(akReader);
                akReader.Close();

                return myTbl;
            }
            catch (Exception ex)
            { }
            return null;
        }

        public Int64 ReadCount(DatasetConfig datasetConfig)
        {
            try
            {
                CheckAndEstablishReadConnection();

                readCmd.Connection = readDBConn;
                readCmd.CommandText = "SELECT COUNT(*) FROM " + datasetConfig.Name;

                return Convert.ToInt64(readCmd.ExecuteScalar());
            }
            catch (Exception ex)
            { }
            return 0;
        }

        #endregion
    }
}
