﻿using System;
using System.Data;
using Microsoft.SqlServer.Server;
using System.Data.SqlTypes;
using System.Data.SqlClient;
using System.Text;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Globalization;
using System.Reflection;
using System.Threading;
using System.ComponentModel.Design;

/*
Title:  SendSBMsg
Author: Mitch van Huuksloot, 
        Data SQL Ninja Team

The Sample Code below was developed by Microsoft Corporation technical architects.  Because Microsoft must respond to changing market conditions, this document should not be interpreted as an invitation to contract or a commitment on the part of Microsoft.
Microsoft has provided high level guidance in this artifact with the understanding that the reader would undertake detailed design and comprehensive reviews of the overall solution before the solution would be implemented/delivered.  
Microsoft is not responsible for the final implementation undertaken.  
MICROSOFT MAKES NO WARRANTIES, EXPRESS OR IMPLIED WITH RESPECT TO THE INFORMATION CONTAINED HEREIN.  
*/

public class CLRTriggers
{
    private const string URI = "https://mvhsbpoc.servicebus.windows.net/tablechange";
    private const string Namespace = "mvhsbpoc.servicebus.windows.net";
    private const string KeyName = "RootManageSharedAccessKey";
    private const string AccountKey = "5VHeguGPSAKbd5fROzPbYjGsMeo5FiFGLNT6Pbfan2X=";
    private const string hashqry = "WITH cols as ( "+
                                        "select top 100000 c.object_id, column_id, c.[name] "+
                                        "from sys.columns c "+
                                            "JOIN sys.objects ot on (c.object_id= ot.parent_object_id and ot.type= 'TA') " +
                                        "order by c.object_id, column_id ) "+
                                    "SELECT s.[name] + '.' + o.[name] as 'TableName', CONVERT(NCHAR(32), HASHBYTES('MD5',STRING_AGG(CONVERT(NCHAR(32), HASHBYTES('MD5', cols.[name]), 2), '|')),2) as 'MD5Hash' " +
                                    "FROM cols "+
                                        "JOIN sys.objects o on (cols.object_id= o.object_id) "+
                                        "JOIN sys.schemas s on (o.schema_id= s.schema_id) "+
                                    "WHERE o.is_ms_shipped = 0 "+
                                    "GROUP BY s.[name], o.[name]";

    // [SqlTrigger(Name = @"SendSBMsg", Target = "[dbo].[table]", Event = "FOR INSERT, UPDATE")]
    public static void trgSendSBMsg()
    {
#if DEBUG
        DateTime start = DateTime.Now;
#endif
        string table = "", server = "", msgbody;
        SqlCommand cmd;
        SqlDataReader rdr;
        SqlTriggerContext trigContxt = SqlContext.TriggerContext;
        SqlPipe p = SqlContext.Pipe;

        using (SqlConnection con = new SqlConnection("context connection=true"))
        {
            // build message to send - we match the XML generated by the TSQL DML triggers (i.e. "FOR XML PATH" format - although we could easily produce an easier XML string to parse at the other end).
            try
            {
                con.Open();

                // We need to get the table name somehow, since it is not in either the SqlContext or the SqlTriggerContext :-(
                // One work around is to look at the table your session has locked, but we think that is fragile - think transactions on multiple tables or holdlock etc.
                // We use a somewhat expensive work around that uses an MD5 hash of the column names from the inserted or deleted tables and compares it to the column name hashes of tables in the current database with CLR triggers.
                // For efficiency, the code to calculate the hashes is embedded in the rest of the trigger processing.
                // In theory we could also look at the plan of the currently executing statement and pull the table from that, but that is likely much more overhead.

                MD5 hash = MD5.Create();
                StringBuilder hashstr = new StringBuilder(250);
                string sqlcmd, tblhash = "", rows = "</table><rows>";
                if (trigContxt.TriggerAction == TriggerAction.Delete)
                {
                    sqlcmd = "SELECT * FROM DELETED";
                    msgbody = "<message><server>" + server + "</server><action>delete</action><table>";
                }
                else                                                                                                        // insert or update - the target figures out which is necessary
                {
                    sqlcmd = "SELECT * FROM INSERTED";
                    msgbody = "<message><server>" + server + "</server><action>insert</action><table>";
                }
                using (cmd = new SqlCommand(sqlcmd, con))
                {
                    using (rdr = cmd.ExecuteReader(CommandBehavior.SingleResult))
                    {
                        while (rdr.Read())
                        {
                            rows += "<row>";
                            for (int i = 0; i < rdr.FieldCount; i++)
                            {
                                string colname = rdr.GetName(i);
                                if (tblhash.Length == 0)                                                                    // only calculate the column name hash for the first record
                                {
                                    if (i > 0) hashstr.Append("|");                                                         // use a pipe separator
                                    hashstr.Append(GetMD5Hash(hash, colname));                                              // append the hash to the hash string
                                }
                                rows += "<" + colname + ">" + rdr.GetValue(i).ToString() + "</" + colname + ">";
                            }
                            rows += "</row>";
                            if (tblhash.Length == 0) tblhash = GetMD5Hash(hash, hashstr.ToString().ToUpper()).ToUpper();    // hash the hash string to reduce the string size to 32 characters
                        }
                    }
                    rdr.Close();
                }
                using (cmd = new SqlCommand(hashqry, con))
                {
                    using (rdr = cmd.ExecuteReader(CommandBehavior.SingleResult))                                           // get the hashes for all tables with CLR triggers
                    {
                        while (rdr.Read())
                        {
                            string shash = rdr.GetString(1).ToUpper();                                                      // get the hash string from sql 
                            if (shash == tblhash)                                                                           // if it matches, we have our table
                            {
                                table = rdr.GetString(0);                                                                   // get the table name
                                break;
                            }
                        }
                        rdr.Close();
                    }
                }
                if (table.Length == 0)
                {
                    p.Send("Error: Unable to find table that CLR trigger is on. Message not sent!");
                    return;
                }
                msgbody += table + rows + "</rows></message>";
            }
            catch (Exception e)
            {
                p.Send("Exception: " + e.Message);
                return;
            }
            con.Close();
#if DEBUG
            p.Send("Message: " + msgbody.Substring(0,3500));
            p.Send("Elapsed Time (ms): " + DateTime.Now.Subtract(start).TotalMilliseconds.ToString());
#endif
        }

        //send message to Service Bus
        try
        { 
            string sasToken = GetSasToken();
            WebClient webClient = new WebClient();
            webClient.Headers[HttpRequestHeader.Authorization] = sasToken;
            webClient.Headers[HttpRequestHeader.ContentType] = "application/atom+xml;type=entry;charset=utf-8";
            var body = Encoding.UTF8.GetBytes(msgbody);
            webClient.UploadData(URI + "/messages", "POST", body);
            string httpmsg;
            int status = GetStatusCode(webClient, out httpmsg);
            if (status > 299) p.Send(status.ToString() + ": " + httpmsg);
        }
        catch (WebException ex)
        {
            p.Send("Web Exception: " + GetErrorFromException(ex));
        }
        catch (Exception ex)
        {
            p.Send("Exception: " + ex.Message);
        }
    }

    private static string GetMD5Hash(MD5 md5Hash, string input)
    {
        byte[] data = md5Hash.ComputeHash(Encoding.Unicode.GetBytes(input));
        StringBuilder sBuilder = new StringBuilder();
        for (int i = 0; i < data.Length; i++)
            sBuilder.Append(data[i].ToString("x2"));
        return sBuilder.ToString();
    }

    // The following functions were borrowed from Jensen Somers - https://jsomers.be/archive/2018/12/20/sending-messages-to-azure-from-sql-server-part-1/
    private static string GetSasToken()
    {
        // Set token lifetime to 20 minutes. 
        DateTime origin = new DateTime(1970, 1, 1, 0, 0, 0, 0);
        TimeSpan diff = DateTime.Now.ToUniversalTime() - origin;
        uint tokenExpirationTime = Convert.ToUInt32(diff.TotalSeconds) + (20 * 60);
        string stringToSign = WebUtility.UrlEncode(Namespace) + "\n" + tokenExpirationTime.ToString();
        var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(AccountKey));
        var signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign)));
        return string.Format(CultureInfo.InvariantCulture, "SharedAccessSignature sr={0}&sig={1}&se={2}&skn={3}", WebUtility.UrlEncode(Namespace), WebUtility.UrlEncode(signature), tokenExpirationTime, KeyName);
    }

    private static int GetStatusCode(WebClient client, out string statusDescription)
    {
        FieldInfo responseField = client.GetType().GetField("m_WebResponse", BindingFlags.Instance | BindingFlags.NonPublic);
        if (responseField != null)
        {
            HttpWebResponse response = responseField.GetValue(client) as HttpWebResponse;
            if (response != null)
            {
                statusDescription = response.StatusDescription;
                return (int)response.StatusCode;
            }
        }
        statusDescription = null;
        return 0;
    }

    private static string GetErrorFromException(WebException webExcp)
    {
        var exceptionMessage = webExcp.Message;

        try
        {
            var httpResponse = (HttpWebResponse)webExcp.Response;
            var stream = httpResponse.GetResponseStream();
            var memoryStream = new MemoryStream();

            stream.CopyTo(memoryStream);

            var receivedBytes = memoryStream.ToArray();
            exceptionMessage = Encoding.UTF8.GetString(receivedBytes)
              + " (HttpStatusCode "
              + httpResponse.StatusCode.ToString()
              + ")";
        }
        catch (Exception ex)
        {
            exceptionMessage = ex.Message;
        }

        return exceptionMessage;
    }

}
