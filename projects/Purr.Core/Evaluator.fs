module Purr.Core.Evaluator

open Purr.Core.Ast
open Purr.Core.Runtime
open Purr.Core.Common
open System.Reflection

type Generator<'i, 'o, 'r> =
  | Yield of value: 'o * continuation: ('i -> Generator<'i, 'o, 'r>)
  | Fail of reason: string
  | Done of value: 'r


module Generator =
  let greturn x = Yield(x, (fun v -> Done v))

  let gyield x k = Yield(x, k)

  let gfail reason = Fail(reason)

  let rec bind g f =
    match g with
    | Yield (v, k) -> gyield v (fun i -> bind (k i) f)
    | Fail reason -> Fail reason
    | Done r -> f r

  let map f g = bind g (fun x -> greturn (f x))

  let sequence m1 m2 =
    bind m1 (fun _ -> m2)


  let rec runToValue g =
    match g with
    | Done r -> r
    | Fail reason -> failwith reason
    | Yield (v, k) -> runToValue (k v)

  let rec runToHalt g =
    match g with
    | Done r -> Done r
    | Fail e -> Fail e
    | Yield (v, k) -> runToHalt (k v)

  let next g =
    match g with
    | Done r -> (r, Done r)
    | Fail e -> failwith e
    | Yield (v, k) -> (v, k v)

  let fromResult r =
    match r with
    | Ok v -> greturn v
    | Error e -> Fail e
  

type GeneratorBuilder() =
  member __.Bind(m, f) = Generator.bind m f
  member __.Return(v) = Generator.greturn v
  member __.ReturnFrom(m) = m


let gen = GeneratorBuilder()

let ensureDiscarded value =
  match value with
  | Nothing -> Done Nothing
  | _ -> Generator.gfail 
            """
            Only the last expression in a sequence may return a value.
            
            If you have `a; b`, and `a` returns a value, you must explicitly ignore it:
            
                a |> ignore;
                b
            """

let rec evalAexpr env expr =
  match expr with
  | AExpr.Text v -> 
      Ok <| text v

  | AExpr.Integer v ->
      Ok <| integer v

  | AExpr.Float v ->
      Ok <| float v

  | AExpr.Boolean v ->
      Ok <| bool v

  | AExpr.Nothing ->
      Ok <| nothing

  | AExpr.List items ->
      let rec go xs =
        match xs with
        | x :: xs -> 
          result {
            let! x = evalAexpr env x
            let! xs = go xs
            return cons x xs
          }
        | [] -> Ok nothing
      in go items

  | AExpr.Lambda (parameters, body) ->
      Ok <| closure env parameters body

  | AExpr.LoadLocal name ->
      match Environment.lookup name env with
      | Some v -> Ok <| v
      | None -> Error (sprintf "%s is not defined" name)
  
and evalManyAexpr env exprs =
  List.foldBack
    (fun expr xs -> result {
                      let! xs = xs
                      let! expr = evalAexpr env expr
                      return expr :: xs
                    })
    exprs
    (Ok [])

and evalCexpr env expr =
  match expr with
  | CExpr.If (test, consequent, alternate) ->
      gen {
        let! test = Generator.fromResult (evalAexpr env test)
        if asBoolean test then
          return! evalExpr env consequent
        else
          return! evalExpr env alternate
      }
  
  | CExpr.Apply (callee, args) ->
      gen {
        let! callee = Generator.fromResult (evalAexpr env callee)
        match evalManyAexpr env args with
        | Error e -> return! Fail e
        | Ok args ->
            match callee with
            | Closure(cEnv, parameters, body) ->
                let newEnv = Environment.extend (List.zip parameters args) cEnv
                if (List.length parameters <> List.length args) then
                  return! Fail (sprintf "Expected %d arguments, got %d"
                                 (List.length parameters) 
                                 (List.length args))
                else
                  return! evalExpr newEnv body
            | NativeProcedure(fn) ->
                return fn args
            | _ -> 
                return! Fail (sprintf "Apply expects a Function, got %s" (purrType callee))
      }

  | CExpr.LoadModule name ->
      gen {
        match env.Module.Lookup(name) with
        | Some (Evaluated value) -> 
            return value
        | Some (Unevaluated expr) ->
            let bareEnv = Environment.empty env.Module
            let! value = evalExpr bareEnv expr
            env.Module.Update(name, Evaluated value)
            return value
        | None ->
            return! Fail (sprintf "No top-level definition for %s" name)
      }

  | CExpr.AExpr expr -> Generator.fromResult (evalAexpr env expr)

and evalExpr env expr =
  match expr with
  | Expr.Let (name, init, body) ->
      gen {
        let! init = evalCexpr env init
        let newEnv = Environment.extend [name, init] env
        return! evalExpr newEnv body
      }

  | Expr.CExpr expr -> evalCexpr env expr
  | Expr.AExpr expr -> Generator.fromResult (evalAexpr env expr)

  