module Server

open System
open System.Net
open System.Threading
open System.IO

[<AutoOpen>]
module HttpExtensions = 
  type System.Net.HttpListener with
    member x.AsyncGetContext() = 
      Async.FromBeginEnd(x.BeginGetContext, x.EndGetContext)

/// HttpAgent that listens for HTTP requests and handles
/// them using the function provided to the Start method
type HttpAgent private (url, f) as this =
  let tokenSource = new CancellationTokenSource()
  let agent = MailboxProcessor.Start((fun _ -> f this), tokenSource.Token)
  let server = async { 
    use listener = new HttpListener()
    listener.Prefixes.Add(url)
    listener.Start()
    while true do 
      let! context = listener.AsyncGetContext()
      agent.Post(context)
      match context.Request.ContentType with
      | "POST" -> agent.Post(context) 
      | _ -> () }
  do Async.Start(server, cancellationToken = tokenSource.Token)

  /// Asynchronously waits for the next incomming HTTP request
  /// The method should only be used from the body of the agent
  member x.Receive(?timeout) = agent.Receive(?timeout = timeout)

  /// Stops the HTTP server and releases the TCP connection
  member x.Stop() = tokenSource.Cancel()

  /// Starts new HTTP server on the specified URL. The specified
  /// function represents computation running inside the agent.
  static member Start(url, f) = new HttpAgent(url, f)

[<EntryPoint>]
let main argv = 
    let url = "http://localhost:8085/pricing/"
//    let trials = pown 2 16

    let server = HttpAgent.Start(url, fun server -> async {
        while true do 
            let! ctx = server.Receive()
            let tradeId = ctx.Request.QueryString.Get("trade_id")
            let body = (new StreamReader(ctx.Request.InputStream)).ReadToEnd()

            Console.WriteLine(body)
            MarketData.MarketDataProvider.Parse body
            |> MarketData.MarketContainer.FromJSon
            |> fun marketData -> (marketData, Contracts.JSonProductLoader marketData (Contracts.ContractProvider.GetSamples()))
            |> fun (marketData, products) -> 
                [for product in products -> "{ \"price\":" + string((MonteCarlo.Price marketData product)) + " }"]
            |> fun prices -> ("[\n " + (String.Join(",\n", prices) + " \n]"))
            |> System.Text.Encoding.UTF8.GetBytes
            |> fun price -> (ctx.Response.OutputStream.Write(price, 0, price.Length))
            
            ctx.Response.Close()
    })

    // Stop the HTTP server and release the port 8082
    Console.WriteLine("Server ready on " + url)
    Console.ReadLine() |> ignore
    server.Stop()
    0