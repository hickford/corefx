// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

using size_t = System.UInt64;
using SafeCurlHandle = Interop.libcurl.SafeCurlHandle;
using SafeCurlMultiHandle = Interop.libcurl.SafeCurlMultiHandle;
using curlioerr = Interop.libcurl.curlioerr;
using curliocmd = Interop.libcurl.curliocmd;
using CURLINFO = Interop.libcurl.CURLINFO;
using CURLcode = Interop.libcurl.CURLcode;
using CURLMcode = Interop.libcurl.CURLMcode;
using curl_socket_t = System.Int32;
using CurlPoll = Interop.libcurl.CurlPoll;
using CurlSelect = Interop.libcurl.CurlSelect;
using CURLAUTH = Interop.libcurl.CURLAUTH;
using CURLoption = Interop.libcurl.CURLoption;

namespace System.Net.Http
{
    internal partial class CurlHandler
    {
        private const string s_httpPrefix = "HTTP/";
        private static readonly char[] s_newLineCharArray = new char[] { HttpRuleParser.CR, HttpRuleParser.LF };
        private const char SpaceChar = ' ';
        private const int StatusCodeLength = 3;

        private static Interop.libcurl.curl_readwrite_callback s_receiveHeadersCallback =
            (Interop.libcurl.curl_readwrite_callback) CurlReceiveHeadersCallback;
        private unsafe static Interop.libcurl.curl_unsafe_write_callback s_receiveBodyCallback =
            (Interop.libcurl.curl_unsafe_write_callback) CurlReceiveBodyCallback;
        private static Interop.libcurl.curl_readwrite_callback s_sendCallback =
            (Interop.libcurl.curl_readwrite_callback) CurlSendCallback;
        private static Interop.libcurl.curl_ioctl_callback s_sendIoCtlCallback =
            (Interop.libcurl.curl_ioctl_callback) CurlSendIoCtlCallback;
        private static Interop.libcurl.curl_socket_callback s_socketCallback =
            (Interop.libcurl.curl_socket_callback) CurlSocketCallback;
        private static Interop.libcurl.curl_multi_timer_callback s_multiTimerCallback =
            (Interop.libcurl.curl_multi_timer_callback) CurlMultiTimerCallback;

        private static size_t CurlReceiveHeadersCallback(
            IntPtr buffer,
            size_t size,
            size_t nitems,
            IntPtr context)
        {
            return ExecuteCallback(buffer, size, nitems, context,
                (ptr, totalSize, state) =>
                {
                    state.CancellationToken.ThrowIfCancellationRequested();

                    // The callback is invoked once per header and multi-line
                    // headers get merged into a single line
                    string responseHeader = Marshal.PtrToStringAnsi(ptr).Trim();
                    HttpResponseMessage response = state.ResponseMessage;

                    // TODO: Understand scenarios where multiple sets of headers are received
                    if (!TryParseStatusLine(response, responseHeader, state))
                    {
                        int colonIndex = responseHeader.IndexOf(':');

                        // Skip malformed header lines that are missing the colon character.
                        if (colonIndex > 0)
                        {
                            string headerName = responseHeader.Substring(0, colonIndex);
                            string headerValue = responseHeader.Substring(colonIndex + 1);

                            if (!response.Headers.TryAddWithoutValidation(headerName, headerValue))
                            {
                                // TODO: Skip compression related headers
                                response.Content.Headers.TryAddWithoutValidation(headerName, headerValue);
                            }
                        }
                    }
                    return totalSize;
                });
        }

        private unsafe static size_t CurlReceiveBodyCallback(
            byte* buffer,
            size_t size,
            size_t nitems,
            IntPtr context)
        {
            size *= nitems;
            GCHandle gch = GCHandle.FromIntPtr(context);
            var state = (RequestCompletionSource)gch.Target;
            try
            {
                // Set task completion after all headers have been received
                // TODO: Fail if we find that task is already Canceled or Faulted
                state.TrySetResult(state.ResponseMessage);

                // Wait for a reader
                // TODO: The below call blocks till all the data has been read since
                //       response body is not suppored to be buffered in memory.
                //       Figure out some way to work around this
                CurlResponseStream contentStream = state.ResponseMessage.ContentStream;
                contentStream.WaitAndSignalReaders(buffer, (long)size);
                return size;
            }
            catch (Exception ex)
            {
                HandleAsyncException(state, ex);
                // Returing a value other than size fails the callback and forces
                // request completion with an error
                return (size > 0) ? size - 1 : 1;
            }
        }

        private static size_t CurlSendCallback(
            IntPtr buffer,
            size_t size,
            size_t nitems,
            IntPtr context)
        {
            return ExecuteCallback(buffer, size, nitems, context,
                (ptr, totalSize, state) =>
                {
                    state.CancellationToken.ThrowIfCancellationRequested();
                    Stream contentStream = state.RequestContentStream;

                    // TODO: Handle chunked-mode

                    byte[] byteBuffer = state.RequestContentBuffer;
                    int numBytes = contentStream.Read(state.RequestContentBuffer, 0, Math.Min(byteBuffer.Length, (int)totalSize));
                    if (numBytes > 0)
                    {
                        Marshal.Copy(byteBuffer, 0, ptr, numBytes);
                    }
                    return (size_t)numBytes;
                });
        }

        private static int CurlSendIoCtlCallback(
            IntPtr handle,
            int cmd,
            IntPtr context)
        {
            if (cmd != curliocmd.CURLIOCMD_RESTARTREAD)
            {
                return curlioerr.CURLIOE_UNKNOWNCMD;
            }

            GCHandle gch = GCHandle.FromIntPtr(context);
            var state = (RequestCompletionSource)gch.Target;

            try
            {
                if (state.CancellationToken.IsCancellationRequested)
                {
                    HandleAsyncException(state, null);
                    // Returing a value other than CURLIOE_OK fails the callback and forces
                    // request completion with an error
                    return curlioerr.CURLIOE_FAILRESTART;
                }

                Stream contentStream = state.RequestContentStream;
                if (!contentStream.CanSeek)
                {
                    return curlioerr.CURLIOE_FAILRESTART;
                }
                contentStream.Seek(0, SeekOrigin.Begin);
                return curlioerr.CURLIOE_OK;
            }
            catch (Exception ex)
            {
                HandleAsyncException(state, ex);
                // Returing a value other than CURLIOE_OK fails the callback and forces
                // request completion with an error
                return curlioerr.CURLIOE_FAILRESTART;
            }
        }

        private static size_t ExecuteCallback(
            IntPtr buffer,
            size_t size,
            size_t nitems,
            IntPtr context,
            Func<IntPtr, size_t, RequestCompletionSource, size_t> callback)
        {
            size *= nitems;
            GCHandle gch = GCHandle.FromIntPtr(context);
            var state = (RequestCompletionSource)gch.Target;

            try
            {
                return (size == 0) ? 0 : callback(buffer, size, state);
            }
            catch (Exception ex)
            {
                HandleAsyncException(state, ex);
                // Returing a value other than size fails the callback and forces
                // request completion with an error
                return (size > 0) ? size - 1 : 1;
            }
        }

        private static bool TryParseStatusLine(HttpResponseMessage response, string responseHeader, RequestCompletionSource state)
        {
            if (!responseHeader.StartsWith(s_httpPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Clear the header if status line is recieved again. This signifies that there are multiple response headers (like in redirection).
            response.Headers.Clear();
            
            response.Content.Headers.Clear();        

            int responseHeaderLength = responseHeader.Length;

            // Check if line begins with HTTP/1.1 or HTTP/1.0
            int prefixLength = s_httpPrefix.Length;
            int versionIndex = prefixLength + 2;

            if ((versionIndex < responseHeaderLength) && (responseHeader[prefixLength] == '1') && (responseHeader[prefixLength + 1] == '.'))
            {
                if (responseHeader[versionIndex] == '1')
                {
                    response.Version = HttpVersion.Version11;
                }
                else if (responseHeader[versionIndex] == '0')
                {
                    response.Version = HttpVersion.Version10;
                }
                else
                {
                    response.Version = new Version(0, 0);
                }
            }
            else
            {
                response.Version = new Version(0, 0);
            }
         
            // TODO: Parsing errors are treated as fatal. Find right behaviour
           
            int spaceIndex = responseHeader.IndexOf(SpaceChar);

            if (spaceIndex > -1)
            {
                int codeStartIndex = spaceIndex + 1;              
                int statusCode = 0;

                // Parse first 3 characters after a space as status code
                if (TryParseStatusCode(responseHeader, codeStartIndex, out statusCode))
                {
                    response.StatusCode = (HttpStatusCode)statusCode;

                    // For security reasons, we drop the server credential if it is a
                    // NetworkCredential.  But we allow credentials in a CredentialCache
                    // since they are specifically tied to URI's.
                    if ((response.StatusCode == HttpStatusCode.Redirect) && !(state.Handler.Credentials is CredentialCache))
                    {
                        state.Handler.SetCurlOption(state.RequestHandle, CURLoption.CURLOPT_HTTPAUTH, CURLAUTH.None);
                        state.Handler.SetCurlOption(state.RequestHandle, CURLoption.CURLOPT_USERNAME, IntPtr.Zero );
                        state.Handler.SetCurlOption(state.RequestHandle, CURLoption.CURLOPT_PASSWORD, IntPtr.Zero);
                        state.NetworkCredential = null;
                    }

                    int codeEndIndex = codeStartIndex + StatusCodeLength;

                    int reasonPhraseIndex = codeEndIndex + 1;

                    if (reasonPhraseIndex < responseHeaderLength && responseHeader[codeEndIndex] == SpaceChar)
                    {
                        int newLineCharIndex = responseHeader.IndexOfAny(s_newLineCharArray, reasonPhraseIndex);
                        int reasonPhraseEnd = newLineCharIndex >= 0 ? newLineCharIndex : responseHeaderLength;
                        response.ReasonPhrase = responseHeader.Substring(reasonPhraseIndex, reasonPhraseEnd - reasonPhraseIndex);
                    }
                }               
            }
            
            return true;
        }

        private static bool TryParseStatusCode(string responseHeader, int statusCodeStartIndex, out int statusCode)
        {          
            if (statusCodeStartIndex + StatusCodeLength > responseHeader.Length)
            {
                statusCode = 0;
                return false;
            }

            char c100 = responseHeader[statusCodeStartIndex];
            char c10 = responseHeader[statusCodeStartIndex + 1];
            char c1 = responseHeader[statusCodeStartIndex + 2];

            if (c100 < '0' || c100 > '9' ||
                c10 < '0' || c10 > '9' ||
                c1 < '0' || c1 > '9')
            {
                statusCode = 0;
                return false;
            }

            statusCode =  (c100 - '0') * 100 +  (c10 - '0') * 10 +  (c1 - '0');

            return true;
        }

        private static int CurlSocketCallback(
            IntPtr handle,
            curl_socket_t socketFd,
            int socketAction,
            IntPtr context,
            IntPtr sockPtr)
        {
            int retVal = CURLMcode.CURLM_OK;

            if (CurlPoll.CURL_POLL_REMOVE == socketAction)
            {
                return retVal;
            }

            bool isAdd = false;
            if (IntPtr.Zero == sockPtr)
            {
                int result = Interop.libcurl.curl_easy_getinfo(handle, CURLINFO.CURLINFO_PRIVATE, out sockPtr);
                if (result != CURLcode.CURLE_OK)
                {
                    throw new HttpRequestException(SR.net_http_client_execution_error, GetCurlException(result, false));
                }
                isAdd = true;
            }

            RequestCompletionSource state;
            try
            {
                GCHandle stateHandle = GCHandle.FromIntPtr(sockPtr);
                state = (RequestCompletionSource) stateHandle.Target;
            }
            catch
            {
                // Sometimes callbacks are invoked even after handle has been cleaned up
                return retVal;
            }

            if (isAdd)
            {
                lock (state.SessionHandle)
                {
                    int result = Interop.libcurl.curl_multi_assign(state.SessionHandle, socketFd, sockPtr);
                    if (result != CURLMcode.CURLM_OK)
                    {
                        throw new HttpRequestException(SR.net_http_client_execution_error,
                            GetCurlException(result, true));
                    }
                }
            }
            else
            {
                int eventMask = CurlSelect.CURL_CSELECT_IN | CurlSelect.CURL_CSELECT_OUT;
                CheckForCompletedTransfers(state.SessionHandle, socketFd, eventMask, sockPtr);
            }

            return retVal;
        }

        private static int CurlMultiTimerCallback(
            IntPtr handle,
            long timeoutInMilliseconds,
            IntPtr context)
        {
            // TODO: Handle <=0 values for the timeout
            Task.Delay((int)timeoutInMilliseconds).ContinueWithStandard(context, (t, c) => CurlTimerElapsed((IntPtr)c));

            return CURLMcode.CURLM_OK;
        }

        private static void CurlTimerElapsed(
            IntPtr context)
        {
            try
            {
                GCHandle gch = GCHandle.FromIntPtr(context);
                SafeCurlMultiHandle multiHandle = (SafeCurlMultiHandle)gch.Target;
                CheckForCompletedTransfers(multiHandle, -1, 0, IntPtr.Zero);
            }
            catch
            {
                // CurlHandler was probably disposed
            }
        }

        private static void CheckForCompletedTransfers(SafeCurlMultiHandle multiHandle, int socketFd, int eventBitMask, IntPtr statePtr)
        {
            int runningTransfers;
            // TODO: Revisit the lock and see if serialization is really needed
            lock (multiHandle)
            {
                int result = Interop.libcurl.curl_multi_socket_action(multiHandle, socketFd, eventBitMask,
                    out runningTransfers);
                if (result != CURLMcode.CURLM_OK)
                {
                    throw new HttpRequestException(SR.net_http_client_execution_error, GetCurlException(result, true));
                }
            }

            // Let socket callback handle fd-specific message
            if (IntPtr.Zero == statePtr)
            {
                return;
            }

            int pendingMessages;
            IntPtr messagePtr;
            lock (multiHandle)
            {
                messagePtr = Interop.libcurl.curl_multi_info_read(multiHandle, out pendingMessages);
            }
            while (IntPtr.Zero != messagePtr)
            {
                var message = Marshal.PtrToStructure<Interop.libcurl.CURLMsg>(messagePtr);
                if (Interop.libcurl.CURLMSG.CURLMSG_DONE == message.msg)
                {
                    EndRequest(multiHandle, statePtr, message.result);
                }
                lock (multiHandle)
                {
                    messagePtr = Interop.libcurl.curl_multi_info_read(multiHandle, out pendingMessages);
                }
            }
        }
    }
}