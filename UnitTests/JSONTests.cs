using Microsoft.VisualStudio.TestTools.UnitTesting;
using nmf_view;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace nmf_view
{
    [TestClass()]
    public class JSONTests
    {
        [TestMethod()]
        public void JsonDecodeTest()
        {
            JSON.JSONParseErrors oErrors;
            object o = JSON.JsonDecode("\"This is a plain string\"", out oErrors);
            Assert.AreNotEqual(o, null);
            Assert.AreEqual(o as String, "This is a plain string");
        }
    }
}