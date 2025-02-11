﻿module WorkflowDemo.Device.PipeServer

open System
open System.Threading.Tasks.Dataflow
open MBrace.FsPickler

type ClientHubQueueMsg<'msg> = 
    | Msg of 'msg
    | Stop

type ClientHubQueueDispatch<'msg> = ClientHubQueueMsg<'msg> -> unit

type ClientDispatch<'msg> = 'msg -> unit

type ClientHubMsg<'msg> = 
    | Start of Option<ClientDispatch<'msg> -> unit>
    | NewClient of int * ClientHubQueueDispatch<'msg>
    | RemoveClient of int
    | Broadcast of 'msg
    | StopHub of AsyncReplyChannel<unit>

type ClientHubState<'msg> = {
    clients: List<int * ClientHubQueueDispatch<'msg>>
    newClientHandler: Option<ClientDispatch<'msg> -> unit>
}

let private log = NLog.LogManager.GetLogger "PipeServer"

type ClientHub<'outMsg, 'inMsg>(pipeName: string) = 
    
    let msgRecvd = Event<'inMsg> ()

    let agent = MailboxProcessor<ClientHubMsg<'outMsg>>.Start (fun inbox ->
        inbox.Error.Add (fun exc -> printfn "Got exception in client hub agent: %A" exc)

        let rec loop state =
            async {
                match! inbox.Receive () with
                | Start handler ->
                    log.Info "ClientHub starting"
                    let rec handleConnections id = 
                        async {
                            log.Info (sprintf "Opening pipe with name '%s', id = %d" pipeName id)
                            let pipe = new IO.Pipes.NamedPipeServerStream (pipeName, IO.Pipes.PipeDirection.InOut, -1)
                            log.Info "Pipe open, waiting for connection"
                            do! pipe.WaitForConnectionAsync () |> Async.AwaitTask        
                            log.Info (sprintf "Got client connection for %d" id)
                            let queue = BufferBlock<ClientHubQueueMsg<'outMsg>> ()  
                            let pickler = FsPickler.CreateBinarySerializer ()
                            async {
                                try
                                    log.Info "Starting serializer"
                                    pickler.SerializeSequence (
                                        pipe, 
                                        () |> Seq.unfold (fun _ -> 
                                            let msg = queue.Receive ()
                                            match msg with
                                            | Stop -> None
                                            | Msg pipeMsg -> Some (pipeMsg, ())
                                        )
                                    ) |> ignore
                                with
                                | _ ->
                                    log.Info (sprintf "Pipe %d closing" id)
                                    pipe.Close ()
                                    inbox.Post (RemoveClient id)                                
                            } |> Async.Start      
                            async {
                                try 
                                    log.Info "Starting deserializer"
                                    for msg in pickler.DeserializeSequence<'inMsg>(pipe) do
                                        msgRecvd.Trigger(msg)
                                with
                                | _ -> () //pipe close is handled by the sending async
                            } |> Async.Start
                            log.Info (sprintf "Sending new client message for %d" id)
                            inbox.Post (NewClient (id, (queue.Post >> ignore)))
                            return! handleConnections (id + 1)   
                        } 
                    handleConnections 0 |> Async.Start
                    return! loop {state with newClientHandler = handler}
                | NewClient (id, client) ->
                    log.Info (sprintf "Got new client %d" id)
                    state.newClientHandler |> Option.iter (fun h -> h (ClientHubQueueMsg.Msg >> client))
                    return! loop {state with clients = (id, client) :: state.clients}
                | RemoveClient id ->
                    log.Info (sprintf "Removing client %d" id)
                    let newClients = state.clients |> List.filter (fun (clientId, _) -> id <> clientId)
                    return! loop {state with clients = newClients}                       
                | Broadcast msg ->
                    for (_, client) in state.clients do
                        client (Msg msg)
                    return! loop state                           
                | StopHub reply ->
                    log.Info "Stopping client hub"
                    for (_, client) in state.clients do
                        client Stop
                    reply.Reply ()
                    return! loop {state with clients = []}
            }
        loop {clients = []; newClientHandler = None}
    )

    member __.Start ?newClientHandler = agent.Post (Start newClientHandler)
    member __.Broadcast msg = agent.Post (Broadcast msg)
    member __.Stop () = agent.PostAndReply StopHub
    member __.MsgRecvd = msgRecvd.Publish

