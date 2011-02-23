//-----------------------------------------------------------------------------
//
// Copyright (C) Microsoft Corporation.  All Rights Reserved.
//
//-----------------------------------------------------------------------------
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.IO;
//using ExternalProver;
using System.Linq;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using Microsoft.Boogie.AbstractInterpretation;
using Microsoft.Boogie;
using Microsoft.Boogie.VCExprAST;
using Microsoft.Boogie.Clustering;
using Microsoft.Boogie.TypeErasure;
using Microsoft.Boogie.Simplify;
using System.Text;

namespace Microsoft.Boogie.SMTLib
{
  public class SMTLibProcessTheoremProver : ProverInterface
  {
    private readonly SMTLibProverContext ctx;
    private readonly VCExpressionGenerator gen;
    private readonly SMTLibProverOptions options;

    [ContractInvariantMethod]
    void ObjectInvariant()
    {
      Contract.Invariant(ctx != null);
      Contract.Invariant(AxBuilder != null);
      Contract.Invariant(Namer != null);
      Contract.Invariant(DeclCollector != null);
      Contract.Invariant(cce.NonNullElements(Axioms));
      Contract.Invariant(cce.NonNullElements(TypeDecls));
      Contract.Invariant(_backgroundPredicates != null);

    }


    [NotDelayed]
    public SMTLibProcessTheoremProver(ProverOptions options, VCExpressionGenerator gen,
                                      SMTLibProverContext ctx)
    {
      Contract.Requires(options != null);
      Contract.Requires(gen != null);
      Contract.Requires(ctx != null);
      InitializeGlobalInformation("UnivBackPred2.smt2");

      this.options = (SMTLibProverOptions)options;
      this.ctx = ctx;
      this.gen = gen;

      TypeAxiomBuilder axBuilder;
      switch (CommandLineOptions.Clo.TypeEncodingMethod) {
        case CommandLineOptions.TypeEncoding.Arguments:
          axBuilder = new TypeAxiomBuilderArguments(gen);
          axBuilder.Setup();
          break;
        case CommandLineOptions.TypeEncoding.Monomorphic:
          axBuilder = new TypeAxiomBuilderPremisses(gen);
          break;
        default:
          axBuilder = new TypeAxiomBuilderPremisses(gen);
          axBuilder.Setup();
          break;
      }

      AxBuilder = axBuilder;
      Namer = new SMTLibNamer();
      ctx.Namer = Namer;
      this.DeclCollector = new TypeDeclCollector((SMTLibProverOptions)options, Namer);

      if (this.options.UseZ3) {
        var psi = SMTLibProcess.ComputerProcessStartInfo(Z3.ExecutablePath(), "AUTO_CONFIG=false -smt2 -in");
        Process = new SMTLibProcess(psi, this.options);
        Process.ErrorHandler += this.HandleProverError;
      }
    }

    public override ProverContext Context
    {
      get
      {
        Contract.Ensures(Contract.Result<ProverContext>() != null);

        return ctx;
      }
    }

    readonly TypeAxiomBuilder AxBuilder;
    readonly UniqueNamer Namer;
    readonly TypeDeclCollector DeclCollector;
    readonly SMTLibProcess Process;
    readonly List<string> proverErrors = new List<string>();
    readonly List<string> proverWarnings = new List<string>();
    readonly StringBuilder common = new StringBuilder();
    TextWriter currentLogFile;
    volatile ErrorHandler currentErrorHandler;

    private void FeedTypeDeclsToProver()
    {
      foreach (string s in DeclCollector.GetNewDeclarations()) {
        Contract.Assert(s != null);
        AddTypeDecl(s);
      }
    }

    private string Sanitize(string msg)
    {
      var idx = msg.IndexOf('\n');
      if (idx > 0)
        msg = msg.Replace("\r", "").Replace("\n", "\r\n");
      return msg;
    }

    private void SendCommon(string s)
    {
      Send(s, true);
    }

    private void SendThisVC(string s)
    {
      Send(s, false);
    }

    private void Send(string s, bool isCommon)
    {
      s = Sanitize(s);

      if (isCommon)
        common.Append(s).Append("\r\n");

      if (Process != null)
        Process.Send(s);
      if (currentLogFile != null)
        currentLogFile.WriteLine(s);
    }

    private void PrepareCommon()
    {
      if (common.Length == 0) {
        SendCommon("(set-option :print-success false)");
        SendCommon("(set-info :smt-lib-version 2.0)");
        if (options.ExpectingModel())
          SendCommon("(set-option :produce-models true)");
        foreach (var opt in options.SmtOptions) {
          SendCommon("(set-option :" + opt.Option + " " + opt.Value + ")");
        }
        SendCommon("; done setting options\n");
        SendCommon(_backgroundPredicates);
      }

      if (!AxiomsAreSetup) {
        var axioms = ctx.Axioms;
        var nary = axioms as VCExprNAry;
        if (nary != null && nary.Op == VCExpressionGenerator.AndOp)
          foreach (var expr in nary.UniformArguments) {
            var str = VCExpr2String(expr, -1);
            if (str != "true")
              AddAxiom(str);
          } else
          AddAxiom(VCExpr2String(axioms, -1));
        AxiomsAreSetup = true;
      }
    }

    private void FlushAxioms()
    {
      TypeDecls.Iter(SendCommon);
      TypeDecls.Clear();
      foreach (string s in Axioms) {
        Contract.Assert(s != null);
        if (s != "true")
          SendCommon("(assert " + s + ")");
      }
      Axioms.Clear();
      FlushPushedAssertions();
    }

    private void CloseLogFile()
    {
      if (currentLogFile != null) {
        currentLogFile.Close();
        currentLogFile = null;
      }
    }

    private void FlushLogFile()
    {
      if (currentLogFile != null) {
        currentLogFile.Flush();
      }
    }

    public override void BeginCheck(string descriptiveName, VCExpr vc, ErrorHandler handler)
    {
      //Contract.Requires(descriptiveName != null);
      //Contract.Requires(vc != null);
      //Contract.Requires(handler != null);

      if (options.SeparateLogFiles) CloseLogFile(); // shouldn't really happen

      if (options.LogFilename != null && currentLogFile == null) {
        currentLogFile = OpenOutputFile(descriptiveName);
        currentLogFile.Write(common.ToString());
      }

      PrepareCommon();
      string vcString = "(assert (not\n" + VCExpr2String(vc, 1) + "\n))";
      FlushAxioms();

      SendThisVC("(push 1)");
      SendThisVC("(set-info :boogie-vc-id " + SMTLibNamer.QuoteId(descriptiveName) + ")");
      SendThisVC(vcString);
      FlushLogFile();

      if (Process != null)
        Process.PingPong(); // flush any errors

      SendThisVC("(check-sat)");
      FlushLogFile();
    }

    private static Dictionary<string, bool> usedLogNames = new Dictionary<string, bool>();

    private TextWriter OpenOutputFile(string descriptiveName)
    {
      Contract.Requires(descriptiveName != null);
      Contract.Ensures(Contract.Result<TextWriter>() != null);

      string filename = options.LogFilename;
      filename = Helpers.SubstituteAtPROC(descriptiveName, cce.NonNull(filename));
      var curFilename = filename;

      lock (usedLogNames) {
        int n = 1;
        while (usedLogNames.ContainsKey(curFilename)) {
          curFilename = filename + "." + n++;
        }
        usedLogNames[curFilename] = true;
      }

      return new StreamWriter(curFilename, false);
    }

    private void FlushProverWarnings()
    {
      var handler = currentErrorHandler;
      if (handler != null) {
        lock (proverWarnings) {
          proverWarnings.Iter(handler.OnProverWarning);
          proverWarnings.Clear();
        }
      }
    }

    private void HandleProverError(string s)
    {
      s = s.Replace("\r", "");
      lock (proverWarnings) {
        while (s.StartsWith("WARNING: ")) {
          var idx = s.IndexOf('\n');
          var warn = s;
          if (idx > 0) {
            warn = s.Substring(0, idx);
            s = s.Substring(idx + 1);
          } else {
            s = "";
          }
          warn = warn.Substring(9);
          proverWarnings.Add(warn);
        }
      }

      FlushProverWarnings();

      if (s == "") return;

      lock (proverErrors) {
        proverErrors.Add(s);
        Console.WriteLine("Prover error: " + s);
      }
    }

    [NoDefaultContract]
    public override Outcome CheckOutcome(ErrorHandler handler)
    {  //Contract.Requires(handler != null);
      Contract.EnsuresOnThrow<UnexpectedProverOutputException>(true);
      
      var result = Outcome.Undetermined;

      if (Process == null)
        return result;

      try {
        currentErrorHandler = handler;
        FlushProverWarnings();

        var errorsLeft = CommandLineOptions.Clo.ProverCCLimit;
        if (errorsLeft < 1)
          errorsLeft = 1;

        var globalResult = Outcome.Undetermined;

        while (errorsLeft-- > 0) {
          string negLabel = null;

          result = GetResponse();
          if (globalResult == Outcome.Undetermined)
            globalResult = result;

          if (result == Outcome.Invalid && options.UseZ3) {
            negLabel = GetLabelsInfo(handler);
          }

          if (negLabel == null) break;

          SendThisVC("(assert " + SMTLibNamer.QuoteId(SMTLibNamer.BlockedLabel(negLabel)) + ")");
          SendThisVC("(check-sat)");
        }

        SendThisVC("(pop 1)");
        FlushLogFile();

        return globalResult;

      } finally {
        currentErrorHandler = null;
      }
    }

    private string GetLabelsInfo(ErrorHandler handler)
    {
      SendThisVC("(get-info :labels)");
      if (options.ExpectingModel())
        SendThisVC("(get-info :model)");
      Process.Ping();

      string negLabel = null;
      List<string> labelNums = null;
      Model theModel = null;

      while (true) {
        var resp = Process.GetProverResponse();
        if (resp == null || Process.IsPong(resp))
          break;
        if (resp.Name == ":labels" && resp.ArgCount >= 1) {
          var labels = resp[0].Arguments.Select(a => a.Name.Replace("|", "")).ToArray();
          negLabel = labels.FirstOrDefault(l => l.StartsWith("@"));
          if (labelNums != null) HandleProverError("Got multiple :labels responses");
          labelNums = labels.Select(a => a.Replace("@", "").Replace("+", "")).ToList();          
          if (negLabel == null)
            HandleProverError("No negative label in: " + labels.Concat(" "));
        } else if (resp.Name == ":model" && resp.ArgCount >= 1) {
          var modelStr = resp[0].Name;
          List<Model> models = null;
          try {
            models = Model.ParseModels(new StringReader("Z3 error model: \n" + modelStr));
          } catch (ArgumentException exn) {
            HandleProverError("Model parsing error: " + exn.Message);
          }

          if (models != null) {
            if (models.Count == 0) HandleProverError("Could not parse any models");
            else {
              if (models.Count > 1) HandleProverError("Expecting only one model, got multiple");
              if (theModel != null) HandleProverError("Got multiple :model responses");
              theModel = models[0];
            }
          }
        }
      }

      if (labelNums != null) {
        ErrorModel m = null;
        if (theModel != null)
          m = new ErrorModel(theModel);
        handler.OnModel(labelNums, m);
      }

      return negLabel;
    }

    private Outcome GetResponse()
    {
      var result = Outcome.Undetermined;

      Process.Ping();

      while (true) {
        var resp = Process.GetProverResponse();
        if (resp == null || Process.IsPong(resp))
          break;

        switch (resp.Name) {
          case "unsat":
            result = Outcome.Valid;
            break;
          case "sat":
          case "unknown":
            result = Outcome.Invalid;
            break;
          default:
            HandleProverError("Unexpected prover response: " + resp.ToString());
            break;
        }
      }
      return result;
    }

    protected string VCExpr2String(VCExpr expr, int polarity)
    {
      Contract.Requires(expr != null);
      Contract.Ensures(Contract.Result<string>() != null);

      DateTime start = DateTime.Now;
      //if (CommandLineOptions.Clo.Trace)
      //  Console.Write("Linearising ... ");

      // handle the types in the VCExpr
      TypeEraser eraser;
      switch (CommandLineOptions.Clo.TypeEncodingMethod) {
        case CommandLineOptions.TypeEncoding.Arguments:
          eraser = new TypeEraserArguments((TypeAxiomBuilderArguments)AxBuilder, gen);
          break;
        case CommandLineOptions.TypeEncoding.Monomorphic:
          eraser = null;
          break;
        default:
          eraser = new TypeEraserPremisses((TypeAxiomBuilderPremisses)AxBuilder, gen);
          break;
      }
      VCExpr exprWithoutTypes = eraser == null ? expr : eraser.Erase(expr, polarity);
      Contract.Assert(exprWithoutTypes != null);

      LetBindingSorter letSorter = new LetBindingSorter(gen);
      Contract.Assert(letSorter != null);
      VCExpr sortedExpr = letSorter.Mutate(exprWithoutTypes, true);
      Contract.Assert(sortedExpr != null);
      VCExpr sortedAxioms = letSorter.Mutate(AxBuilder.GetNewAxioms(), true);
      Contract.Assert(sortedAxioms != null);

      DeclCollector.Collect(sortedAxioms);
      DeclCollector.Collect(sortedExpr);
      FeedTypeDeclsToProver();

      AddAxiom(SMTLibExprLineariser.ToString(sortedAxioms, Namer, options));
      string res = SMTLibExprLineariser.ToString(sortedExpr, Namer, options);
      Contract.Assert(res != null);

      if (CommandLineOptions.Clo.Trace) {
        DateTime end = DateTime.Now;
        TimeSpan elapsed = end - start;
        if (elapsed.TotalSeconds > 0.5)
          Console.WriteLine("Linearising   [{0} s]", elapsed.TotalSeconds);
      }
      return res;
    }

    // the list of all known axioms, where have to be included in each
    // verification condition
    private readonly List<string/*!>!*/> Axioms = new List<string/*!*/>();
    private bool AxiomsAreSetup = false;




    // similarly, a list of function/predicate declarations
    private readonly List<string/*!>!*/> TypeDecls = new List<string/*!*/>();

    protected void AddAxiom(string axiom)
    {
      Contract.Requires(axiom != null);
      Axioms.Add(axiom);
      //      if (thmProver != null) {
      //        LogActivity(":assume " + axiom);
      //        thmProver.AddAxioms(axiom);
      //      }
    }

    protected void AddTypeDecl(string decl)
    {
      Contract.Requires(decl != null);
      TypeDecls.Add(decl);
      //     if (thmProver != null) {
      //       LogActivity(decl);
      //       thmProver.Feed(decl, 0);
      //     }
    }

    ////////////////////////////////////////////////////////////////////////////

    private static string _backgroundPredicates;

    static void InitializeGlobalInformation(string backgroundPred)
    {
      Contract.Requires(backgroundPred != null);
      Contract.Ensures(_backgroundPredicates != null);
      //throws ProverException, System.IO.FileNotFoundException;
      if (_backgroundPredicates == null) {
        string codebaseString =
          cce.NonNull(Path.GetDirectoryName(cce.NonNull(System.Reflection.Assembly.GetExecutingAssembly().Location)));

        // Initialize '_backgroundPredicates'
        string univBackPredPath = Path.Combine(codebaseString, backgroundPred);
        using (StreamReader reader = new System.IO.StreamReader(univBackPredPath)) {
          _backgroundPredicates = reader.ReadToEnd();
        }
      }
    }

    public override VCExpressionGenerator VCExprGen
    {
      get { return this.gen; }
    }

    //// Push/pop interface
    List<string> pushedAssertions = new List<string>();
    int numRealPushes;
    public override string VCExpressionToString(VCExpr vc)
    {
      return VCExpr2String(vc, 1);
    }

    public override void PushVCExpression(VCExpr vc)
    {
      pushedAssertions.Add(VCExpressionToString(vc));
    }

    public override void Pop()
    {
      if (pushedAssertions.Count > 0) {
        pushedAssertions.RemoveRange(pushedAssertions.Count - 1, 1);
      } else {
        Contract.Assert(numRealPushes > 0);
        numRealPushes--;
        SendThisVC("(pop 1)");
      }
    }

    public override int NumAxiomsPushed()
    {
      return numRealPushes + pushedAssertions.Count;
    }

    private void FlushPushedAssertions()
    {
      foreach (var a in pushedAssertions) {
        SendThisVC("(push 1)");
        SendThisVC("(assert " + a + ")");
        numRealPushes++;
      }
      pushedAssertions.Clear();
    }
  }

  public class SMTLibProverContext : DeclFreeProverContext
  {
    internal UniqueNamer Namer;

    public SMTLibProverContext(VCExpressionGenerator gen,
                               VCGenerationOptions genOptions)
      : base(gen, genOptions)
    {
    }

    protected SMTLibProverContext(SMTLibProverContext par)
      : base(par)
    {
      if (par.Namer != null)
        this.Namer = (UniqueNamer)par.Namer; // .Clone();
    }

    public override object Clone()
    {
      return new SMTLibProverContext(this);
    }

    public override string Lookup(VCExprVar var)
    {
      return Namer.Lookup(var);
    }
  }

  public class Factory : ProverFactory
  {
    public override object SpawnProver(ProverOptions options, object ctxt)
    {
      //Contract.Requires(ctxt != null);
      //Contract.Requires(options != null);
      Contract.Ensures(Contract.Result<object>() != null);

      return this.SpawnProver(options,
                              cce.NonNull((SMTLibProverContext)ctxt).ExprGen,
                              cce.NonNull((SMTLibProverContext)ctxt));
    }

    public override object NewProverContext(ProverOptions options)
    {
      //Contract.Requires(options != null);
      Contract.Ensures(Contract.Result<object>() != null);

      VCExpressionGenerator gen = new VCExpressionGenerator();
      List<string>/*!>!*/ proverCommands = new List<string/*!*/>();
      proverCommands.Add("all");
      proverCommands.Add("smtlib");
      var opts = (SMTLibProverOptions)options ;
      if (opts.UseZ3)
        proverCommands.Add("z3");
      VCGenerationOptions genOptions = new VCGenerationOptions(proverCommands);
      return new SMTLibProverContext(gen, genOptions);
    }

    public override ProverOptions BlankProverOptions()
    {
      return new SMTLibProverOptions();
    }

    protected virtual SMTLibProcessTheoremProver SpawnProver(ProverOptions options,
                                                              VCExpressionGenerator gen,
                                                              SMTLibProverContext ctx)
    {
      Contract.Requires(options != null);
      Contract.Requires(gen != null);
      Contract.Requires(ctx != null);
      Contract.Ensures(Contract.Result<SMTLibProcessTheoremProver>() != null);

      return new SMTLibProcessTheoremProver(options, gen, ctx);
    }
  }
}
