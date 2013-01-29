﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace QuranPhone.Data
{
    public class BaseDatabaseHandler : IDisposable
    {
        protected SQLiteDatabase mDatabase = null;

        public void CloseDatabase()
        {
            if (mDatabase != null)
            {
                mDatabase.Close();
                mDatabase = null;
            }
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
            CloseDatabase();
        }
    }
}