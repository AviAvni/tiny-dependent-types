(*
----------------------------------------------------------------------
Copyright (c) 2012 David Raymond Christiansen

Permission is hereby granted, free of charge, to any person
obtaining a copy of this software and associated documentation
files (the "Software"), to deal in the Software without
restriction, including without limitation the rights to use, copy,
modify, merge, publish, distribute, sublicense, and/or sell copies
of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

 * The above copyright notice and this permission notice shall be
  included in all copies or substantial portions of the Software.

 * The software is provided "as is", without warranty of any kind,
  express or implied, including but not limited to the warranties of
  merchantability, fitness for a particular purpose and
  noninfringement.  In no event shall the authors or copyright
  holders be liable for any claim, damages or other liability,
  whether in an action of contract, tort or otherwise, arising from,
  out of or in connection with the software or the use or other
  dealings in the software.
----------------------------------------------------------------------
*)

module Toplevel

open Microsoft.FSharp.Text.Lexing
open System
open Gnu.Getopt

open Nat
open AST
open Result
open Typechecker
open Utils
open Microsoft.FSharp.Text
open Mono.Terminal

type state = {
    globals: Global.env;
    debug: bool
  }

let stateContains (s : state) (x : string) : (string * term * term) option =
  let rec findName = function
    | [] -> None
    | (x', tm, tp) :: _ when x = x' -> Some (x', tm, tp)
    | _ :: defs -> findName defs
  match s.globals with
    | Global.Env defs -> findName defs

let postulate x tp s =
  match s.globals with
    Global.Env defs ->
      { s with globals = Global.Env (snoc defs (x, Postulated (x, tp), tp)) }

let define (x : string) (tm : term) (tp : term) (s : state) : state =
  match s.globals with
    Global.Env defs -> {s with globals =  Global.Env <| snoc defs (x, tm, tp)}

let defineCheck x tm s =
  res {
    let! tp = typecheck emptyEnv s.globals tm
    let s' = define x tm tp s
    return s'
  }

let parse (lexbuf : Lexing.LexBuffer<char>) : command result =
  try
    lexbuf |> Grammar.command Lexical.token |> Result.Success
  with
    | exn -> let pos = lexbuf.EndPos
             in Failure <| lazy (sprintf "%s near line %d, column %d\n"
                                    (exn.Message) (pos.Line+1) pos.Column)

let parseTerm (lexbuf : Lexing.LexBuffer<char>) : term result =
  try
    lexbuf |> Grammar.exprTop Lexical.token |> Result.Success
  with
    | exn -> let pos = lexbuf.EndPos
             in Failure <| lazy (sprintf "%s near line %d, column %d\n"
                                    (exn.Message) (pos.Line+1) pos.Column)

let evaluate state expr =
  res {
    let! typ = typecheck emptyEnv state.globals expr
    let! result = normalize state.globals expr
    if state.debug
    then return (sprintf "%A" result, sprintf "%A" typ)
    else return (pprintTerm result, pprintTerm typ)
  } |> Result.fold
         (fun (x, y) ->
           printfn "  %s  :  %s" x y)
         (fun msg -> printf " Error: %s\n" (msg.Force()))

let rec loop (le : LineEditor) (s : state) : int =
  printf "\n"
  let input = le.Edit("> ","")

  if s.debug
  then Lexical.printLex input
       Lexical.lex input
       |> parse
       |> Result.fold (printfn "Parse result: %A")
            (fun msg -> printfn "Parse error: %s" (msg.Force()))

  try
    Lexical.lex input
    |> parse
    |> Result.bind (handleCmd s)
    |> Result.fold (loop le) (fun err -> printfn "%s" (err.Force()) ; loop le s)
  with
    | exn -> printfn "Exception during command execution: %A" exn.Message
             if s.debug then printfn "Stack trace:\n%s" exn.StackTrace
             loop le s

and handleCmd (s : state) (cmd : command) : state result =
  match cmd with
    | Eval e -> evaluate s e ; Success s
    | Postulate (x, ty) -> Success (postulate x ty s)
    | ShowState -> showState s ; Success s
    | Load x -> printfn "loading %A..." x ;
                loadFile s x
    | DataDef (name, args, univ, cases) -> doDefineData s name args univ cases
    | Def (x, t) -> doDefine s x t
    | ToggleDebug -> printfn "Debugging is now %s" (if not s.debug then "ON" else "OFF") ;
                     Success {s with debug = not s.debug}
    | Quit -> System.Environment.Exit(0) ; Failure <| lazy "exiting failed"

and handleCmds (s : state) (cmds : command list) : state result =
  match cmds with
    | [] -> Success s
    | c :: cs -> res {
        let! newState = handleCmd s c
        let! rest = handleCmds newState cs
        return rest
      }

and showState (s : state) : unit =
  match s.globals with
    Global.Env ss ->
      for (x, defn, ty) in ss do
        printfn "%s  =  %s  :  %s" x (pprintTerm defn) (pprintTerm ty)

and loadFile (s : state) (filename : string) : state result =
  let contents = System.IO.File.ReadAllText(filename)
  let lexbuf = Lexical.lex contents
  try
    lexbuf |> Grammar.file Lexical.token |> handleCmds s
  with
    | exn -> lazy (let pos = lexbuf.EndPos
                   sprintf "%s near line %d, column %d\n"
                       (exn.Message) (pos.Line+1) pos.Column)
             |> Failure

and doDefine (s : state) (x : string) (t : term) : state result =
  match stateContains s x with
    | None ->
      res {
        let! typ = typecheck emptyEnv s.globals t
        let! result = normalize s.globals t
        return define x result typ s
      }
    | Some (x, tm, tp) ->
      Failure
      <| lazy (sprintf "%s already defined as %s  :  %s" x (pprintTerm tm) (pprintTerm tp))


and doDefineData (s : state) typename typeargs univ cases =
  let defineType s =
    res {
      let! tSig = Result.mapList (fun (a, t) -> Result.map (fun x -> a, x)
                                                  (normalize s.globals t)) typeargs
      return {
          datatype.name = typename
          datatype.signature = tSig
        }
    }
  let makeConstruct (s : state) (resT : datatype) (aCase : case) : construct result =
    match aCase with
      Case (cname, cargs, resargs) ->
        res {
          let! cargs' = Result.mapList (fun (a, t) -> Result.map (fun x -> a, x)
                                                        (normalize s.globals t)) cargs
          do! Result.failIf (resargs.Length = 0 || resargs.Head <> Free (resT.name))
                <| lazy (sprintf "Constructors must construct their datatype.")
          return { name = cname
                   signature = cargs'
                   result = (resT, resargs.Tail)
                 }
        }
  res {
    let! newT = defineType s
    let! s' = defineCheck newT.name (Datatype (newT, [])) s
    let! constrs = Result.mapList (makeConstruct s' newT) cases
    return! addInductive s newT constrs
  }

and addInductive (s : state) (t : datatype) (cs : construct list) : state result =
  let defineConstr (s : state) (c : construct) : state result =
    res {
      let name = c.name
      let tm = Constructor (c, [])
      let! s' = defineCheck name tm s
      return s'
    }
  let rec defineConstrs (s : state) (constrs : construct list) : state result =
    match constrs with
      | [] -> Success s
      | c :: cs -> res {
          let! s' = defineConstr s c
          let! s'' = defineConstrs s' cs
          return s''
        }
  res {
    let! withType = defineCheck t.name (Datatype (t, [])) s
    let! withConstrs = defineConstrs withType cs
    let! withElim = defineCheck (t.name + "Elim") (Ind (t, cs, [])) withConstrs
    return withElim
  }

let startState : state =
  let emptyState = {globals = Global.empty ; debug = false}

  loadFile emptyState "prelude"
  |> Result.fold (id)
       (fun err -> printfn "Could not load prelude.\nThere is no stdlib.\n Error: %s" (err.Force())
                   emptyState)

type options = {
    showHelp : bool
    eval : string list
  }

let defaultOpts = { showHelp = false ; eval = [] }

let getOptions (args : string []) : options result =
  let longOptions : LongOpt [] =
    [| new LongOpt("help", Argument.No, null, int 'h')
     ; new LongOpt("eval", Argument.Required, null, int 'e')
     |]

  let g = new Getopt("Toplevel.exe", args, "he:")
  g.Opterr <- false

  let rec processOptions opts =
    let c = g.getopt ()
    if c = -1
    then opts
    else
      res {
        let! currOpts = opts
        let! newOpts =
          match char c with
            | 'h' -> Success { currOpts with showHelp = true }
            | 'e' -> Success { currOpts with eval = snoc currOpts.eval g.Optarg }
            | '?' -> Failure <| lazy (sprintf "Invalid option: %A" <| char g.Optopt)
            | _   -> Failure <| lazy (sprintf "getopt returned %A" <| char c)
        return newOpts
      }

  processOptions <| Success defaultOpts

let help () =
  printfn "Command-line options:"
  printfn "   -h, --help\t\tShow this message"
  Environment.Exit(-1)

let rec evalPrint s = function
  | [] -> Environment.Exit(0)
  | e :: es ->
    Lexical.lex e
    |> parseTerm
    |> Result.fold (evaluate s) (fun err -> printfn "%s" <| err.Force())
    evalPrint s es

[<EntryPoint>]
let main (args : string []) : int =
  let opts = Result.fold id
               (fun err ->
                printfn "%s" <| err.Force()
                help ()
                defaultOpts)
               (getOptions args)

  if opts.showHelp
  then help()

  if opts.eval <> []
  then evalPrint startState opts.eval

  let le = new LineEditor("deptypes")

  printfn "Silly dependent type checker version 0.0.0.\n:q to quit."
  loop le startState


