<%@ Page Language="C#" AutoEventWireup="true" CodeBehind="Default.aspx.cs" Inherits="SampleWebApp.Default" %>

<!DOCTYPE html>

<html xmlns="http://www.w3.org/1999/xhtml">
<head runat="server">
    <title></title>
</head>
<body>
    <form id="form1" runat="server">
    <div>
        <p>
            This simple web application is configured in the web.config to use the AWS Session Provider for session state. The app will increment the page count
            for every click on the refresh button and store the current value in the session. To confirm this an entry will be written to 
            the ASP.NET_SessionState table and can be viewed with the DynamoDB table browser.
        </p>
        <p>
            The application will use the default profile for credentials and the us-west-2 region. When first launched it will 
            create the ASP.NET_SessionState table if it doesn't exist. Be sure to delete this table when done testing to avoid charges.
        </p>
        <p>
        Number of page views <%=this.PageCount%>
        </p>
        <input type="submit" value="Refresh"/>
    </div>
    </form>
</body>
</html>
