document.addEventListener('DOMContentLoaded', function() {
  let portNMH = null;

  function log (sMsg) {
    const newDiv = document.createElement("div");
    newDiv.textContent = sMsg;
    document.getElementById('divIncoming').appendChild(newDiv);
  }

  function nmhSend(oMsg) {
  /*
  When a messaging port is created using runtime.connectNative Chrome starts native messaging host process and keeps it running until the port 
  is destroyed. On the other hand, when a message is sent using runtime.sendNativeMessage, without creating a messaging port, Chrome starts a 
  new native messaging host process for each message. In that case the first message generated by the host process is handled as a response to 
  the original request, i.e. Chrome will pass it to the response callback specified when runtime.sendNativeMessage is called. All other messages 
  generated by the native messaging host in that case are ignored.
  */
    console.log('About to send ' + JSON.stringify(oMsg));
    if (!portNMH) {
      console.log('portNMH was destroyed or not yet created'); return; 
    }
    try {
      portNMH.postMessage( oMsg );
    } catch (e) {
      console.log('! postMessage failed! ' + e.message);
    }
  }

  // Send a message on an existing port.
  document.getElementById('btnPost').addEventListener('click', function(e) {
    nmhSend({ "messagetext": document.getElementById('txtSend').value,
              "source": "monitor.html",
              "timestamp": new Date().toLocaleTimeString()
            });
  }, false);
  
  // Send a one-shot message.
  //
  // TODO: Firefox has a different API signature for |sendNativeMessage|. :(
  document.getElementById('btnSend').addEventListener('click', function(e) {
    chrome.runtime.sendNativeMessage('com.bayden.nmf.demo', { "messagetext": document.getElementById('txtSend').value,
              "source": "monitor.html",
              "timestamp": new Date().toLocaleTimeString()
            },
            (r)=>log('Got one-shot answer:'+JSON.stringify(r)));
  }, false);

  document.getElementById('btnDisconnect').addEventListener('click', function(e) {
    portNMH.disconnect();
  }, false);

  document.getElementById('btnNull').addEventListener('click', function(e) {
    console.log("Before delete: " + JSON.stringify(portNMH));
    console.log('onMessage.hasListener: ' + portNMH.onMessage.hasListener(gotMessage));
    console.log('onDisconnect.hasListener: ' + portNMH.onDisconnect.hasListener(didDisconnect));
    portNMH.onDisconnect.removeListener(didDisconnect);
    portNMH.onMessage.removeListener(gotMessage);
    console.log("After removing listeners: " + JSON.stringify(portNMH));
    console.log('onMessage.hasListener: ' + portNMH.onMessage.hasListener(gotMessage));
    console.log('onDisconnect.hasListener: ' + portNMH.onDisconnect.hasListener(didDisconnect));
    portNMH = undefined;
    console.log("After setting to undefined: " + JSON.stringify(portNMH));
  }, false);

  function didDisconnect() {
    log("!!!!NativeMessagingHost.onDisconnect(); " + chrome.runtime.lastError.message);
  }

  function gotMessage(msg) {
    log("[Received Message From NativeHost]: " + JSON.stringify(msg));
  }

  try {
    portNMH = chrome.runtime.connectNative('com.bayden.nmf.demo');
    portNMH.onDisconnect.addListener(didDisconnect);
    portNMH.onMessage.addListener(gotMessage);
  }
  catch (e) {
    log('!!! Failed to connect to nativeMessagingHost! ' + e.message + " " + JSON.stringify(chrome.runtime.lastError));
  }
});
