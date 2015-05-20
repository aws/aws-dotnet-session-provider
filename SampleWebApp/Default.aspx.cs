using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using System.Web.UI;
using System.Web.UI.WebControls;

namespace SampleWebApp
{
    public partial class Default : System.Web.UI.Page
    {
        const string PageCountKey = "PageCount";

        protected void Page_Load(object sender, EventArgs e)
        {

        }

        public int PageCount
        {
            get
            {
                if (this.Session[PageCountKey] == null)
                {
                    this.Session[PageCountKey] = 0;
                }

                int value = Convert.ToInt32(this.Session[PageCountKey]);
                value++;
                this.Session[PageCountKey] = value;
                return value;
            }
        }
    }
}