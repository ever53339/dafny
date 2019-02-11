//-----------------------------------------------------------------------------
//
// Copyright (C) Microsoft Corporation.  All Rights Reserved.
//
//-----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.IO;
using System.Diagnostics.Contracts;
using System.CodeDom.Compiler;
using System.Reflection;
using System.Collections.ObjectModel;
using Bpl = Microsoft.Boogie;



namespace Microsoft.Dafny
{
  public class CsharpCompiler : Compiler
  {
    public CsharpCompiler(ErrorReporter reporter)
    : base(reporter) {
    }

    public override string TargetLanguage => "C#";

    protected override void EmitHeader(Program program, TargetWriter wr) {
      wr.WriteLine("// Dafny program {0} compiled into C#", program.Name);
      wr.WriteLine("// To recompile, use 'csc' with: /r:System.Numerics.dll");
      wr.WriteLine("// and choosing /target:exe or /target:library");
      wr.WriteLine("// You might also want to include compiler switches like:");
      wr.WriteLine("//     /debug /nowarn:0164 /nowarn:0219 /nowarn:1717 /nowarn:0162 /nowarn:0168");
      wr.WriteLine();
      wr.WriteLine("using System;");
      wr.WriteLine("using System.Numerics;");
      EmitDafnySourceAttribute(program, wr);
      ReadRuntimeSystem("DafnyRuntime.cs", wr);
    }

    void EmitDafnySourceAttribute(Program program, TextWriter wr) {
      Contract.Requires(program != null);
      Contract.Requires(wr != null);

      wr.WriteLine("[assembly: DafnyAssembly.DafnySourceAttribute(@\"");

      var strwr = new StringWriter();
      strwr.NewLine = wr.NewLine;
      new Printer(strwr, DafnyOptions.PrintModes.Everything).PrintProgram(program, true);

      wr.Write(strwr.GetStringBuilder().Replace("\"", "\"\"").ToString());
      wr.WriteLine("\")]");
      wr.WriteLine();
    }

    protected override void EmitBuiltInDecls(BuiltIns builtIns, TargetWriter wr) {
      wr = CreateModule("Dafny", wr);
      wr.Indent();
      wr = wr.NewNamedBlock("internal class ArrayHelpers");
      foreach (var decl in builtIns.SystemModule.TopLevelDecls) {
        if (decl is ArrayClassDecl) {
          int dims = ((ArrayClassDecl)decl).Dims;

          // Here is an overloading of the method name, where there is an initialValue parameter
          // public static T[,] InitNewArray2<T>(T z, BigInteger size0, BigInteger size1) {
          wr.Indent();
          wr.Write("public static T[");
          wr.RepeatWrite(dims, "", ",");
          wr.Write("] InitNewArray{0}<T>(T z, ", dims);
          wr.RepeatWrite(dims, "BigInteger size{0}", ", ");
          wr.Write(")");

          var w = wr.NewBlock("");
          // int s0 = (int)size0;
          for (int i = 0; i < dims; i++) {
            w.Indent();
            w.WriteLine("int s{0} = (int)size{0};", i);
          }
          // T[,] a = new T[s0, s1];
          w.Indent();
          w.Write("T[");
          w.RepeatWrite(dims, "", ",");
          w.Write("] a = new T[");
          w.RepeatWrite(dims, "s{0}", ",");
          w.WriteLine("];");
          // for (int i0 = 0; i0 < s0; i0++)
          //   for (int i1 = 0; i1 < s1; i1++)
          for (int i = 0; i < dims; i++) {
            w.IndentExtra(i);
            w.WriteLine("for (int i{0} = 0; i{0} < s{0}; i{0}++)", i);
          }
          // a[i0,i1] = z;
          w.IndentExtra(dims);
          w.Write("a[");
          w.RepeatWrite(dims, "i{0}", ",");
          w.WriteLine("] = z;");
          // return a;
          w.Indent();
          w.WriteLine("return a;");
        }
      }
    }

    protected override BlockTargetWriter CreateModule(string moduleName, TargetWriter wr) {
      var s = string.Format("namespace @{0}", moduleName);
      return wr.NewBigBlock(s, " // end of " + s);
    }

    protected override string GetHelperModuleName() => "Dafny.Helpers";

    string TypeParameters(List<TypeParameter> targs) {
      Contract.Requires(cce.NonNullElements(targs));
      Contract.Ensures(Contract.Result<string>() != null);

      return Util.Comma(targs, tp => "@" + tp.CompileName);
    }

    protected override BlockTargetWriter CreateClass(string name, List<TypeParameter>/*?*/ typeParameters, List<Type>/*?*/ superClasses, Bpl.IToken tok, out TargetWriter instanceFieldsWriter, TargetWriter wr) {
      wr.Indent();
      wr.Write("public partial class {0}", name);
      if (typeParameters != null && typeParameters.Count != 0) {
        wr.Write("<{0}>", TypeParameters(typeParameters));
      }
      if (superClasses != null) {
        string sep = " : ";
        foreach (var trait in superClasses) {
          wr.Write("{0}{1}", sep, TypeName(trait, wr, tok));
          sep = ", ";
        }
      }
      var w = wr.NewBlock("");
      instanceFieldsWriter = w;
      return w;
    }

    protected override BlockTargetWriter CreateTrait(string name, List<Type>/*?*/ superClasses, Bpl.IToken tok, out TargetWriter instanceFieldsWriter, out TargetWriter staticMemberWriter, TargetWriter wr) {
      wr.Indent();
      wr.Write("public interface {0}", IdProtect(name));
      if (superClasses != null) {
        string sep = " : ";
        foreach (var trait in superClasses) {
          wr.Write("{0}{1}", sep, TypeName(trait, wr, tok));
          sep = ", ";
        }
      }
      var w = wr.NewBlock("");
      instanceFieldsWriter = w;

      //writing the _Companion class
      wr.Indent();
      wr.Write("public class _Companion_{0}", name);
      staticMemberWriter = wr.NewBlock("");

      return w;
    }

    protected override void DeclareDatatype(DatatypeDecl dt, TargetWriter wr) {
      CompileDatatypeHeader(dt, wr);
      CompileDatatypeConstructors(dt, wr);
      CompileDatatypeStruct(dt, wr);
    }
    void CompileDatatypeHeader(DatatypeDecl dt, TargetWriter wr) {
      wr.Indent();
      wr.Write("public abstract class Base_{0}", dt.CompileName);
      if (dt.TypeArgs.Count != 0) {
        wr.Write("<{0}>", TypeParameters(dt.TypeArgs));
      }
      wr.WriteLine(" { }");
    }

    void CompileDatatypeConstructors(DatatypeDecl dt, TargetWriter wrx) {
      Contract.Requires(dt != null);

      string typeParams = dt.TypeArgs.Count == 0 ? "" : string.Format("<{0}>", TypeParameters(dt.TypeArgs));
      if (dt is CoDatatypeDecl) {
        // public class Dt__Lazy<T> : Base_Dt<T> {
        //   public delegate Base_Dt<T> Computer();
        //   Computer c;
        //   public Dt__Lazy(Computer c) { this.c = c; }
        //   public Base_Dt<T> Get() { return c(); }
        // }
        wrx.Indent();
        var w = wrx.NewNamedBlock("public class {0}__Lazy{1} : Base_{0}{1}", dt.CompileName, typeParams);
        w.Indent();
        w.WriteLine("public delegate Base_{0}{1} Computer();", dt.CompileName, typeParams);
        w.Indent();
        w.WriteLine("Computer c;");
        w.Indent();
        w.WriteLine("public {0}__Lazy(Computer c) {{ this.c = c; }}", dt.CompileName);
        w.Indent();
        w.WriteLine("public Base_{0}{1} Get() {{ return c(); }}", dt.CompileName, typeParams);
      }

      int constructorIndex = 0; // used to give each constructor a different
      foreach (DatatypeCtor ctor in dt.Ctors) {
        // class Dt_Ctor<T,U> : Base_Dt<T> {
        //   Fields;
        //   public Dt_Ctor(arguments) {
        //     Fields = arguments;
        //   }
        //   public override bool Equals(object other) {
        //     var oth = other as Dt_Dtor;
        //     return oth != null && equals(_field0, oth._field0) && ... ;
        //   }
        //   public override int GetHashCode() {
        //     return base.GetHashCode();  // surely this can be improved
        //   }
        //   public override string ToString() {  // only for inductive datatypes
        //     // ...
        //   }
        // }
        wrx.Indent();
        var wr = wrx.NewNamedBlock("public class {0} : Base_{1}{2}", DtCtorDeclarationName(ctor, dt.TypeArgs), dt.CompileName, typeParams);

        int i = 0;
        foreach (Formal arg in ctor.Formals) {
          if (!arg.IsGhost) {
            wr.Indent();
            wr.WriteLine("public readonly {0} {1};", TypeName(arg.Type, wr, arg.tok), FormalName(arg, i));
            i++;
          }
        }

        wr.Indent();
        wr.Write("public {0}(", DtCtorDeclarationName(ctor));
        WriteFormals("", ctor.Formals, wr);
        using (var w = wr.NewBlock(")")) {
          i = 0;
          foreach (Formal arg in ctor.Formals) {
            if (!arg.IsGhost) {
              w.Indent();
              w.WriteLine("this.{0} = {0};", FormalName(arg, i));
              i++;
            }
          }
        }

        // Equals method
        wr.Indent();
        using (var w = wr.NewBlock("public override bool Equals(object other)")) {
          w.Indent();
          w.Write("var oth = other as {0}", DtCtorName(ctor, dt.TypeArgs));
          w.WriteLine(";");
          w.Indent();
          w.Write("return oth != null");
          i = 0;
          foreach (Formal arg in ctor.Formals) {
            if (!arg.IsGhost) {
              string nm = FormalName(arg, i);
              if (IsDirectlyComparable(arg.Type)) {
                w.Write(" && this.{0} == oth.{0}", nm);
              } else {
                w.Write(" && Dafny.Helpers.AreEqual(this.{0}, oth.{0})", nm);
              }
              i++;
            }
          }
          w.WriteLine(";");
        }

        // GetHashCode method (Uses the djb2 algorithm)
        wr.Indent();
        using (var w = wr.NewBlock("public override int GetHashCode()")) {
          w.Indent(); w.WriteLine("ulong hash = 5381;");
          w.Indent(); w.WriteLine("hash = ((hash << 5) + hash) + {0};", constructorIndex);
          i = 0;
          foreach (Formal arg in ctor.Formals) {
            if (!arg.IsGhost) {
              string nm = FormalName(arg, i);
              w.Indent(); w.WriteLine("hash = ((hash << 5) + hash) + ((ulong)Dafny.Helpers.GetHashCode(this.{0}));", nm);
              i++;
            }
          }
          w.Indent(); w.WriteLine("return (int) hash;");
        }

        if (dt is IndDatatypeDecl) {
          wr.Indent();
          var w = wr.NewBlock("public override string ToString()");
          if (dt is TupleTypeDecl tupleDt && ctor.Formals.Count == 0) {
            // here we want parentheses and no name
            w.Indent(); w.WriteLine("return \"()\";");
          } else {
            string nm;
            if (dt is TupleTypeDecl) {
              nm = "";
            } else {
              nm = (dt.Module.IsDefaultModule ? "" : dt.Module.CompileName + ".") + dt.CompileName + "." + ctor.CompileName;
            }
            var tempVar = GenVarName("s", ctor.Formals);
            w.Indent(); w.WriteLine("string {0} = \"{1}\";", tempVar, nm);
            if (ctor.Formals.Count != 0) {
              w.Indent(); w.WriteLine("{0} += \"(\";", tempVar);
              i = 0;
              foreach (var arg in ctor.Formals) {
                if (!arg.IsGhost) {
                  if (i != 0) {
                    w.Indent(); w.WriteLine("{0} += \", \";", tempVar);
                  }
                  w.Indent(); w.WriteLine("{0} += Dafny.Helpers.ToString(this.{1});", tempVar, FormalName(arg, i));
                  i++;
                }
              }
              w.Indent(); w.WriteLine("{0} += \")\";", tempVar);
            }
            w.Indent(); w.WriteLine("return {0};", tempVar);
          }
        }
      }
      constructorIndex++;
    }

    void CompileDatatypeStruct(DatatypeDecl dt, TargetWriter wr) {
      Contract.Requires(dt != null);
      Contract.Requires(wr != null);

      // public struct Dt<T> : IDatatype{
      //   Base_Dt<T> _d;
      //   public Base_Dt<T> _D {
      //     get {
      //       if (_d == null) {
      //         _d = Default;
      //       } else if (_d is Dt__Lazy<T>) {        // co-datatypes only
      //         _d = ((Dt__Lazy<T>)_d).Get();         // co-datatypes only
      //       }
      //       return _d;
      //     }
      //   }
      //   public Dt(Base_Dt<T> d) { this._d = d; }
      //   static Base_Dt<T> theDefault;
      //   public static Base_Dt<T> Default {
      //     get {
      //       if (theDefault == null) {
      //         theDefault = ...;
      //       }
      //       return theDefault;
      //     }
      //   }
      //   public override bool Equals(object other) {
      //     return other is Dt<T> && _D.Equals(((Dt<T>)other)._D);
      //   }
      //   public override int GetHashCode() { return _D.GetHashCode(); }
      //   public override string ToString() { return _D.ToString(); }  // only for inductive datatypes
      //
      //   public bool is_Ctor0 { get { return _D is Dt_Ctor0; } }
      //   ...
      //
      //   public T0 dtor_Dtor0 { get { return ((DT_Ctor)_D).@Dtor0; } }  // This is in essence what gets generated for the case where the destructor is used in one use constructor
      //   public T0 dtor_Dtor0 { get { var d = _D;                       // This is the general case
      //       if (d is DT_Ctor0) { return ((DT_Ctor0)d).@Dtor0; }
      //       if (d is DT_Ctor1) { return ((DT_Ctor1)d).@Dtor0; }
      //       ...
      //       if (d is DT_Ctor(n-2)) { return ((DT_Ctor(n-2))d).@Dtor0; }
      //       return ((DT_Ctor(n-1))d).@Dtor0;
      //    }}
      //   ...
      // }
      string DtT = dt.CompileName;
      string DtT_protected = IdProtect(DtT);
      string DtT_TypeArgs = "";
      if (dt.TypeArgs.Count != 0) {
        DtT_TypeArgs = "<" + TypeParameters(dt.TypeArgs) + ">";
        DtT += DtT_TypeArgs;
        DtT_protected += DtT_TypeArgs;
      }

      wr.Indent();
      // from here on, write everything into the new block created here:
      wr = wr.NewNamedBlock("public struct {0}", DtT_protected);

      wr.Indent();
      wr.WriteLine("Base_{0} _d;", DtT);

      wr.Indent();
      using (var w = wr.NewNamedBlock("public Base_{0} _D", DtT)) {
        w.Indent();
        var wGet = w.NewBlock("get");
        var wIf = EmitIf("_d == null", dt is CoDatatypeDecl, wGet);
        wIf.Indent();
        wIf.WriteLine("_d = Default;");
        if (dt is CoDatatypeDecl) {
          string typeParams = dt.TypeArgs.Count == 0 ? "" : string.Format("<{0}>", TypeParameters(dt.TypeArgs));
          wIf = EmitIf(string.Format("_d is {0}__Lazy{1}", dt.CompileName, typeParams), false, wGet);
          wIf.Indent();
          wIf.WriteLine("_d = (({0}__Lazy{1})_d).Get();", dt.CompileName, typeParams);
        }
        wGet.Indent(); wGet.WriteLine("return _d;");
      }

      wr.Indent();
      wr.WriteLine("public {0}(Base_{1} d) {{ this._d = d; }}", IdName(dt), DtT);

      wr.Indent();
      wr.WriteLine("static Base_{0} theDefault;", DtT);

      wr.Indent();
      using (var w = wr.NewNamedBlock("public static Base_{0} Default", DtT)) {
        w.Indent();
        var wGet = w.NewBlock("get");
        var wIf = EmitIf("theDefault == null", false, wGet);
        wIf.Indent();
        wIf.Write("theDefault = ");

        DatatypeCtor defaultCtor;
        if (dt is IndDatatypeDecl) {
          defaultCtor = ((IndDatatypeDecl)dt).DefaultCtor;
        } else {
          defaultCtor = ((CoDatatypeDecl)dt).Ctors[0];  // pick any one of them (but pick must be the same as in InitializerIsKnown and HasZeroInitializer)
        }
        wIf.Write("new {0}(", DtCtorName(defaultCtor, dt.TypeArgs));
        string sep = "";
        foreach (Formal f in defaultCtor.Formals) {
          if (!f.IsGhost) {
            wIf.Write("{0}{1}", sep, DefaultValue(f.Type, wIf, f.tok));
            sep = ", ";
          }
        }
        wIf.WriteLine(");");

        wGet.Indent();
        wGet.WriteLine("return theDefault;");
      }

      wr.Indent();
      using (var w = wr.NewNamedBlock("public override bool Equals(object other)")) {
        w.Indent();
        w.WriteLine("return other is {0} && _D.Equals((({0})other)._D);", DtT_protected);
      }

      wr.Indent();
      wr.WriteLine("public override int GetHashCode() { return _D.GetHashCode(); }");
      if (dt is IndDatatypeDecl) {
        wr.Indent();
        wr.WriteLine("public override string ToString() { return _D.ToString(); }");
      }

      // query properties
      foreach (var ctor in dt.Ctors) {
        //   public bool is_Ctor0 { get { return _D is Dt_Ctor0; } }
        wr.Indent();
        wr.WriteLine("public bool is_{0} {{ get {{ return _D is {1}_{0}{2}; }} }}", ctor.CompileName, dt.CompileName, DtT_TypeArgs);
      }
      if (dt.HasFinitePossibleValues) {
        wr.Indent();
        var w = wr.NewNamedBlock("public static System.Collections.Generic.IEnumerable<{0}> AllSingletonConstructors", DtT_protected);
        w.Indent();
        var wGet = w.NewBlock("get");
        foreach (var ctr in dt.Ctors) {
          if (ctr.Formals.Count == 0) {
            wGet.Indent();
            wGet.WriteLine("yield return new {0}(new {2}_{1}());", DtT_protected, ctr.CompileName, dt.CompileName);
          }
        }
        wGet.Indent();
        wGet.WriteLine("yield break;");
      }

      // destructors
      foreach (var ctor in dt.Ctors) {
        foreach (var dtor in ctor.Destructors) {
          if (dtor.EnclosingCtors[0] == ctor) {
            var arg = dtor.CorrespondingFormals[0];
            if (!arg.IsGhost && arg.HasName) {
              wr.Indent();
              //   public T0 dtor_Dtor0 { get { var d = _D;
              //       if (d is DT_Ctor0) { return ((DT_Ctor0)d).@Dtor0; }
              //       if (d is DT_Ctor1) { return ((DT_Ctor1)d).@Dtor0; }
              //       ...
              //       if (d is DT_Ctor(n-2)) { return ((DT_Ctor(n-2))d).@Dtor0; }
              //       return ((DT_Ctor(n-1))d).@Dtor0;
              //    }}
              wr.Write("public {0} dtor_{1} {{ get {{ var d = _D; ", TypeName(arg.Type, wr, arg.tok), arg.CompileName);
              var n = dtor.EnclosingCtors.Count;
              if (n > 1) {
                wr.WriteLine();
              }
              for (int i = 0; i < n-1; i++) {
                var ctor_i = dtor.EnclosingCtors[i];
                Contract.Assert(arg.CompileName == dtor.CorrespondingFormals[i].CompileName);
                wr.IndentExtra(1);
                wr.WriteLine("if (d is {0}_{1}{2}) {{ return (({0}_{1}{2})d).{3}; }}", dt.CompileName, ctor_i.CompileName, DtT_TypeArgs, IdName(arg));
              }
              Contract.Assert(arg.CompileName == dtor.CorrespondingFormals[n-1].CompileName);
              if (n > 1) {
                wr.IndentExtra(1);
              }
              wr.Write("return (({0}_{1}{2})d).{3}; ", dt.CompileName, dtor.EnclosingCtors[n-1].CompileName, DtT_TypeArgs, IdName(arg));
              if (n > 1) {
                wr.WriteLine();
                wr.Indent();
              }
              wr.WriteLine("} }");
            }
          }
        }
      }
    }

    protected override void DeclareNewtype(NewtypeDecl nt, TargetWriter wr) {
      TargetWriter instanceFieldsWriter;
      var w = CreateClass(IdName(nt), null, out instanceFieldsWriter, wr);
      if (nt.NativeType != null) {
        w.Indent();
        var wEnum = w.NewNamedBlock("public static System.Collections.Generic.IEnumerable<{0}> IntegerRange(BigInteger lo, BigInteger hi)", GetNativeTypeName(nt.NativeType));
        wEnum.Indent();
        wEnum.WriteLine("for (var j = lo; j < hi; j++) {{ yield return ({0})j; }}", GetNativeTypeName(nt.NativeType));
      }
      if (nt.WitnessKind == SubsetTypeDecl.WKind.Compiled) { 
        var witness = new TargetWriter();
        TrExpr(nt.Witness, witness, false);
        if (nt.NativeType == null) {
          DeclareField("Witness", true, true, nt.BaseType, nt.tok, witness.ToString(), w);
        } else {
          w.Indent();
          w.Write("public static readonly {0} Witness = ({0})(", GetNativeTypeName(nt.NativeType));
          w.Append(witness);
          w.WriteLine(");");
        }
      }
    }

    protected override void GetNativeInfo(NativeType.Selection sel, out string name, out string literalSuffix, out bool needsCastAfterArithmetic) {
      if (sel == NativeType.Selection.Number) {
        sel = NativeType.Selection.Long;
      }
      base.GetNativeInfo(sel, out name, out literalSuffix, out needsCastAfterArithmetic);
    }

    protected override BlockTargetWriter/*?*/ CreateMethod(Method m, bool createBody, TargetWriter wr) {
      var hasDllImportAttribute = ProcessDllImport(m, wr);
      string targetReturnTypeReplacement = null;
      if (hasDllImportAttribute) {
        foreach (var p in m.Outs) {
          if (!p.IsGhost) {
            if (targetReturnTypeReplacement == null) {
              targetReturnTypeReplacement = TypeName(p.Type, wr, p.tok);
            } else if (targetReturnTypeReplacement != null) {
              // there's more than one out-parameter, so bail
              targetReturnTypeReplacement = null;
              break;
            }
          }
        }
      }

      wr.Indent();
      wr.Write("{0}{1}{2}{3} {4}",
        createBody ? "public " : "",
        m.IsStatic ? "static " : "",
        hasDllImportAttribute ? "extern " : "",
        targetReturnTypeReplacement ?? "void",
        IdName(m));
      if (m.TypeArgs.Count != 0) {
        wr.Write("<{0}>", TypeParameters(m.TypeArgs));
      }
      wr.Write("(");
      int nIns = WriteFormals("", m.Ins, wr);
      if (targetReturnTypeReplacement == null) {
        WriteFormals(nIns == 0 ? "" : ", ", m.Outs, wr);
      }

      if (!createBody || hasDllImportAttribute) {
        wr.WriteLine(");");
        return null;
      } else {
        var w = wr.NewBlock(")");
        w.SetBraceStyle(BlockTargetWriter.BraceStyle.Newline, BlockTargetWriter.BraceStyle.Newline);
        if (m.IsTailRecursive) {
          if (!m.IsStatic) {
            w.Indent(); w.WriteLine("var _this = this;");
          }
          w.IndentExtra(-1); w.WriteLine("TAIL_CALL_START: ;");
        }
        return w;
      }
    }

    protected override BlockTargetWriter/*?*/ CreateFunction(string name, List<TypeParameter>/*?*/ typeArgs, List<Formal> formals, Type resultType, Bpl.IToken tok, bool isStatic, bool createBody, MemberDecl member, TargetWriter wr) {
      var hasDllImportAttribute = ProcessDllImport(member, wr);

      wr.Indent();
      wr.Write("{0}{1}{2}{3} {4}", createBody ? "public " : "", isStatic ? "static " : "", hasDllImportAttribute ? "extern " : "", TypeName(resultType, wr, tok), name);
      if (typeArgs != null && typeArgs.Count != 0) {
        wr.Write("<{0}>", TypeParameters(typeArgs));
      }
      wr.Write("(");
      WriteFormals("", formals, wr);
      if (!createBody || hasDllImportAttribute) {
        wr.WriteLine(");");
        return null;
      } else {
        var w = wr.NewBlock(")");
        if (formals.Count > 1) {
          w.SetBraceStyle(BlockTargetWriter.BraceStyle.Newline, BlockTargetWriter.BraceStyle.Newline);
        }
        return w;
      }
    }

    protected override BlockTargetWriter/*?*/ CreateGetter(string name, Type resultType, Bpl.IToken tok, bool isStatic, bool createBody, TargetWriter wr) {
      wr.Indent();
      wr.Write("{0}{1}{2} {3} {{ get", createBody ? "public " : "", isStatic ? "static " : "", TypeName(resultType, wr, tok), name);
      if (createBody) {
        var w = wr.NewBlock("", " }");
        return w;
      } else {
        wr.WriteLine("; }");
        return null;
      }
    }

    protected override BlockTargetWriter/*?*/ CreateGetterSetter(string name, Type resultType, Bpl.IToken tok, bool isStatic, bool createBody, out TargetWriter setterWriter, TargetWriter wr) {
      wr.Indent();
      wr.Write("{0}{1}{2} {3}", createBody ? "public " : "", isStatic ? "static " : "", TypeName(resultType, wr, tok), name);
      if (createBody) {
        var w = wr.NewBlock("");
        w.Indent();
        var wGet = w.NewBlock("get");
        w.Indent();
        var wSet = w.NewBlock("set");
        setterWriter = wSet;
        return wGet;
      } else {
        wr.WriteLine(" { get; set; }");
        setterWriter = null;
        return null;
      }
    }

    /// <summary>
    /// Process the declaration's "dllimport" attribute, if any, by emitting the corresponding .NET custom attribute.
    /// Returns "true" if the declaration has an active "dllimport" attribute; "false", otherwise.
    /// </summary>
    public bool ProcessDllImport(MemberDecl decl, TargetWriter wr) {
      Contract.Requires(decl != null);
      Contract.Requires(wr != null);

      var dllimportsArgs = Attributes.FindExpressions(decl.Attributes, "dllimport");
      if (!DafnyOptions.O.DisallowExterns && dllimportsArgs != null) {
        StringLiteralExpr libName = null;
        StringLiteralExpr entryPoint = null;
        if (dllimportsArgs.Count == 2) {
          libName = dllimportsArgs[0] as StringLiteralExpr;
          entryPoint = dllimportsArgs[1] as StringLiteralExpr;
        } else if (dllimportsArgs.Count == 1) {
          libName = dllimportsArgs[0] as StringLiteralExpr;
          // use the given name, not the .CompileName (if user needs something else, the user can supply it as a second argument to :dllimport)
          entryPoint = new StringLiteralExpr(decl.tok, decl.Name, false);
        }
        if (libName == null || entryPoint == null) {
          Error(decl.tok, "Expected arguments are {{:dllimport dllName}} or {{:dllimport dllName, entryPoint}} where dllName and entryPoint are strings: {0}", wr, decl.FullName);
        } else if ((decl is Method m && m.Body != null) || (decl is Function f && f.Body != null)) {
          Error(decl.tok, "A {0} declared with :dllimport is not allowed a body: {1}", wr, decl.WhatKind, decl.FullName);
        } else if (!decl.IsStatic) {
          Error(decl.tok, "A {0} declared with :dllimport must be static: {1}", wr, decl.WhatKind, decl.FullName);
        } else {
          wr.Indent();
          wr.Write("[System.Runtime.InteropServices.DllImport(");
          TrStringLiteral(libName, wr);
          wr.Write(", EntryPoint=");
          TrStringLiteral(entryPoint, wr);
          wr.WriteLine(")]");
        }
        return true;
      }
      return false;
    }

    protected override void EmitJumpToTailCallStart(TargetWriter wr) {
      wr.Indent();
      wr.WriteLine("goto TAIL_CALL_START;");
    }

    protected override string TypeName(Type type, TextWriter wr, Bpl.IToken tok) {
      Contract.Requires(type != null);
      Contract.Ensures(Contract.Result<string>() != null);

      var xType = type.NormalizeExpand();
      if (xType is TypeProxy) {
        // unresolved proxy; just treat as ref, since no particular type information is apparently needed for this type
        return "object";
      }

      if (xType is BoolType) {
        return "bool";
      } else if (xType is CharType) {
        return "char";
      } else if (xType is IntType || xType is BigOrdinalType) {
        return "BigInteger";
      } else if (xType is RealType) {
        return "Dafny.BigRational";
      } else if (xType is BitvectorType) {
        var t = (BitvectorType)xType;
        return t.NativeType != null ? GetNativeTypeName(t.NativeType) : "BigInteger";
      } else if (xType.AsNewtype != null) {
        var nativeType = xType.AsNewtype.NativeType;
        if (nativeType != null) {
          return GetNativeTypeName(nativeType);
        }
        return TypeName(xType.AsNewtype.BaseType, wr, tok);
      } else if (xType.IsObjectQ) {
        return "object";
      } else if (xType.IsArrayType) {
        ArrayClassDecl at = xType.AsArrayType;
        Contract.Assert(at != null);  // follows from type.IsArrayType
        Type elType = UserDefinedType.ArrayElementType(xType);
        string typeNameSansBrackets, brackets;
        TypeName_SplitArrayName(elType, wr, tok, out typeNameSansBrackets, out brackets);
        return typeNameSansBrackets + TypeNameArrayBrackets(at.Dims) + brackets;
      } else if (xType is UserDefinedType) {
        var udt = (UserDefinedType)xType;
        var s = udt.FullCompileName;
        var cl = udt.ResolvedClass;
        bool isHandle = true;
        if (cl != null && Attributes.ContainsBool(cl.Attributes, "handle", ref isHandle) && isHandle) {
          return "ulong";
        } else if (DafnyOptions.O.IronDafny &&
            !(xType is ArrowType) &&
            cl != null &&
            cl.Module != null &&
            !cl.Module.IsDefaultModule) {
          s = cl.FullCompileName;
        }
        return TypeName_UDT(s, udt.TypeArgs, wr, udt.tok);
      } else if (xType is SetType) {
        Type argType = ((SetType)xType).Arg;
        if (ComplicatedTypeParameterForCompilation(argType)) {
          Error(tok, "compilation of set<TRAIT> is not supported; consider introducing a ghost", wr);
        }
        return DafnySetClass + "<" + TypeName(argType, wr, tok) + ">";
      } else if (xType is SeqType) {
        Type argType = ((SeqType)xType).Arg;
        if (ComplicatedTypeParameterForCompilation(argType)) {
          Error(tok, "compilation of seq<TRAIT> is not supported; consider introducing a ghost", wr);
        }
        return DafnySeqClass + "<" + TypeName(argType, wr, tok) + ">";
      } else if (xType is MultiSetType) {
        Type argType = ((MultiSetType)xType).Arg;
        if (ComplicatedTypeParameterForCompilation(argType)) {
          Error(tok, "compilation of multiset<TRAIT> is not supported; consider introducing a ghost", wr);
        }
        return DafnyMultiSetClass + "<" + TypeName(argType, wr, tok) + ">";
      } else if (xType is MapType) {
        Type domType = ((MapType)xType).Domain;
        Type ranType = ((MapType)xType).Range;
        if (ComplicatedTypeParameterForCompilation(domType) || ComplicatedTypeParameterForCompilation(ranType)) {
          Error(tok, "compilation of map<TRAIT, _> or map<_, TRAIT> is not supported; consider introducing a ghost", wr);
        }
        return DafnyMapClass + "<" + TypeName(domType, wr, tok) + "," + TypeName(ranType, wr, tok) + ">";
      } else {
        Contract.Assert(false); throw new cce.UnreachableException();  // unexpected type
      }
    }

    public override string TypeInitializationValue(Type type, TextWriter/*?*/ wr, Bpl.IToken/*?*/ tok) {
      var xType = type.NormalizeExpandKeepConstraints();

      if (xType is BoolType) {
        return "false";
      } else if (xType is CharType) {
        return "'D'";
      } else if (xType is IntType || xType is BigOrdinalType) {
        return "BigInteger.Zero";
      } else if (xType is RealType) {
        return "Dafny.BigRational.ZERO";
      } else if (xType is BitvectorType) {
        var t = (BitvectorType)xType;
        return t.NativeType != null ? "0" : "BigInteger.Zero";
      } else if (xType is CollectionType) {
        return TypeName(xType, wr, tok) + ".Empty";
      }

      var udt = (UserDefinedType)xType;
      if (udt.ResolvedParam != null) {
        return "Dafny.Helpers.Default<" + TypeName_UDT(udt.FullCompileName, udt.TypeArgs, wr, udt.tok) + ">()";
      }
      var cl = udt.ResolvedClass;
      Contract.Assert(cl != null);
      if (cl is NewtypeDecl) {
        var td = (NewtypeDecl)cl;
        if (td.Witness != null) {
          return TypeName_UDT(udt.FullCompileName, udt.TypeArgs, wr, udt.tok) + ".Witness";
        } else if (td.NativeType != null) {
          return "0";
        } else {
          return TypeInitializationValue(td.BaseType, wr, tok);
        }
      } else if (cl is SubsetTypeDecl) {
        var td = (SubsetTypeDecl)cl;
        if (td.Witness != null) {
          return TypeName_UDT(udt.FullCompileName, udt.TypeArgs, wr, udt.tok) + ".Witness";
        } else if (td.WitnessKind == SubsetTypeDecl.WKind.Special) {
          // WKind.Special is only used with -->, ->, and non-null types:
          Contract.Assert(ArrowType.IsPartialArrowTypeName(td.Name) || ArrowType.IsTotalArrowTypeName(td.Name) || td is NonNullTypeDecl);
          if (ArrowType.IsPartialArrowTypeName(td.Name)) {
            return string.Format("(({0})null)", TypeName(xType, wr, udt.tok));
          } else if (ArrowType.IsTotalArrowTypeName(td.Name)) {
            var rangeDefaultValue = TypeInitializationValue(udt.TypeArgs.Last(), wr, tok);
            // return the lambda expression ((Ty0 x0, Ty1 x1, Ty2 x2) => rangeDefaultValue)
            return string.Format("(({0}) => {1})",
              Util.Comma(", ", udt.TypeArgs.Count - 1, i => string.Format("{0} x{1}", TypeName(udt.TypeArgs[i], wr, udt.tok), i)),
              rangeDefaultValue);
          } else if (((NonNullTypeDecl)td).Class is ArrayClassDecl) {
            // non-null array type; we know how to initialize them
            var arrayClass = (ArrayClassDecl)((NonNullTypeDecl)td).Class;
            string typeNameSansBrackets, brackets;
            TypeName_SplitArrayName(udt.TypeArgs[0], wr, udt.tok, out typeNameSansBrackets, out brackets);
            return string.Format("new {0}[{1}]{2}", typeNameSansBrackets, Util.Comma(arrayClass.Dims, _ => "0"), brackets);
          } else {
            // non-null (non-array) type
            // even though the type doesn't necessarily have a known initializer, it could be that the the compiler needs to
            // lay down some bits to please the C#'s compiler's different definite-assignment rules.
            return string.Format("default({0})", TypeName(xType, wr, udt.tok));
          }
        } else {
          return TypeInitializationValue(td.RhsWithArgument(udt.TypeArgs), wr, tok);
        }
      } else if (cl is ClassDecl) {
        bool isHandle = true;
        if (Attributes.ContainsBool(cl.Attributes, "handle", ref isHandle) && isHandle) {
          return "0";
        } else {
          return string.Format("({0})null", TypeName(xType, wr, udt.tok));
        }
      } else if (cl is DatatypeDecl) {
        var s = "@" + udt.FullCompileName;
        var rc = cl;
        if (DafnyOptions.O.IronDafny &&
            !(xType is ArrowType) &&
            rc != null &&
            rc.Module != null &&
            !rc.Module.IsDefaultModule) {
          s = "@" + rc.FullCompileName;
        }
        if (udt.TypeArgs.Count != 0) {
          s += "<" + TypeNames(udt.TypeArgs, wr, udt.tok) + ">";
        }
        return string.Format("new {0}()", s);
      } else {
        Contract.Assert(false); throw new cce.UnreachableException();  // unexpected type
      }
    }

    protected override string TypeName_UDT(string fullCompileName, List<Type> typeArgs, TextWriter wr, Bpl.IToken tok) {
      Contract.Requires(fullCompileName != null);
      Contract.Requires(typeArgs != null);
      string s = IdProtect(fullCompileName);
      if (typeArgs.Count != 0) {
        if (typeArgs.Exists(ComplicatedTypeParameterForCompilation)) {
          Error(tok, "compilation does not support trait types as a type parameter; consider introducing a ghost", wr);
        }
        s += "<" + TypeNames(typeArgs, wr, tok) + ">";
      }
      return s;
    }

    protected override string TypeName_Companion(Type type, TextWriter wr, Bpl.IToken tok) {
      var udt = type as UserDefinedType;
      if (udt != null && udt.ResolvedClass is TraitDecl) {
        string s = udt.FullCompanionCompileName;
        if (udt.TypeArgs.Count != 0) {
          if (udt.TypeArgs.Exists(argType => argType.NormalizeExpand().IsObjectQ)) {
            Error(udt.tok, "compilation does not support type 'object' as a type parameter; consider introducing a ghost", wr);
          }
          s += "<" + TypeNames(udt.TypeArgs, wr, udt.tok) + ">";
        }
        return s;
      } else {
        return TypeName(type, wr, tok);
      }
    }

    // ----- Declarations -------------------------------------------------------------

    protected override void DeclareField(string name, bool isStatic, bool isConst, Type type, Bpl.IToken tok, string rhs, TargetWriter wr) {
      wr.Indent();
      wr.WriteLine("public {3}{4}{0} {1} = {2};", TypeName(type, wr, tok), name, rhs,
        isStatic ? "static " : "",
        isConst ? "readonly " : "");
    }

    protected override bool DeclareFormal(string prefix, string name, Type type, Bpl.IToken tok, bool isInParam, TextWriter wr) {
      wr.Write("{0}{1}{2} {3}", prefix, isInParam ? "" : "out ", TypeName(type, wr, tok), name);
      return true;
    }

    protected override void DeclareLocalVar(string name, Type/*?*/ type, Bpl.IToken/*?*/ tok, bool leaveRoomForRhs, string/*?*/ rhs, TargetWriter wr) {
      wr.Indent();
      wr.Write("{0} {1}", type != null ? TypeName(type, wr, tok) : "var", name);
      if (leaveRoomForRhs) {
        Contract.Assert(rhs == null);  // follows from precondition
      } else if (rhs != null) {
        wr.WriteLine(" = {0};", rhs);
      } else {
        wr.WriteLine(";");
      }
    }

    protected override TargetWriter DeclareLocalVar(string name, Type/*?*/ type, Bpl.IToken/*?*/ tok, TargetWriter wr) {
      wr.Indent();
      wr.Write("{0} {1} = ", type != null ? TypeName(type, wr, tok) : "var", name);
      var w = new TargetWriter(wr.IndentLevel);
      wr.Append(w);
      wr.WriteLine(";");
      return w;
    }

    protected override void DeclareOutCollector(string collectorVarName, TargetWriter wr) {
      wr.Write("var {0} = ", collectorVarName);
    }

    protected override void DeclareLocalOutVar(string name, Type type, Bpl.IToken tok, string rhs, TargetWriter wr) {
      EmitAssignment(name, rhs, wr);
    }

    protected override void EmitActualOutArg(string actualOutParamName, TextWriter wr) {
      wr.Write("out {0}", actualOutParamName);
    }

    protected override bool UseReturnStyleOuts(Method m, int nonGhostOutCount) {
      return !DafnyOptions.O.DisallowExterns && Attributes.Contains(m.Attributes, "dllimport") && nonGhostOutCount == 1;
    }

    protected override void EmitOutParameterSplits(string outCollector, List<string> actualOutParamNames, TargetWriter wr) {
      Contract.Assert(actualOutParamNames.Count == 1);
      EmitAssignment(actualOutParamNames[0], outCollector, wr);
    }

    protected override void EmitActualTypeArgs(List<Type> typeArgs, Bpl.IToken tok, TextWriter wr) {
      wr.Write("<" + TypeNames(typeArgs, wr, tok) + ">");
    }

    protected override string GenerateLhsDecl(string target, Type/*?*/ type, TextWriter wr, Bpl.IToken tok) {
      return (type != null ? TypeName(type, wr, tok) : "var") + " " + target;
    }

    // ----- Statements -------------------------------------------------------------

    protected override void EmitPrintStmt(TargetWriter wr, Expression arg) {
      wr.Indent();
      wr.Write("Dafny.Helpers.Print(");
      TrExpr(arg, wr, false);
      wr.WriteLine(");");
    }

    protected override void EmitReturn(List<Formal> outParams, TargetWriter wr) {
      wr.Indent();
      wr.WriteLine("return;");
    }

    protected override TargetWriter CreateLabeledCode(string label, TargetWriter wr) {
      var w = new TargetWriter(wr.IndentLevel);
      wr.Append(w);
      wr.IndentExtra(-1);
      wr.WriteLine("after_{0}: ;", label);
      return w;
    }

    protected override void EmitBreak(string/*?*/ label, TargetWriter wr) {
      wr.Indent();
      if (label == null) {
        wr.WriteLine("break;");
      } else {
        wr.WriteLine("goto after_{0};", label);
      }
    }

    protected override void EmitYield(TargetWriter wr) {
      wr.Indent();
      wr.WriteLine("yield return null;");
    }

    protected override void EmitAbsurd(TargetWriter wr) {
      wr.Indent();
      wr.WriteLine("throw new System.Exception();");
    }

    protected override BlockTargetWriter CreateForLoop(string indexVar, string bound, TargetWriter wr) {
      wr.Indent();
      return wr.NewNamedBlock("for (var {0} = 0; {0} < {1}; {0}++)", indexVar, bound);
    }

    protected override BlockTargetWriter CreateDoublingForLoop(string indexVar, int start, TargetWriter wr) {
      wr.Indent();
      return wr.NewNamedBlock("for (var {0} = new BigInteger({1}); ; {0} *= 2)", indexVar, start);
    }

    protected override void EmitIncrementVar(string varName, TargetWriter wr) {
      wr.Indent();
      wr.WriteLine("{0}++;", varName);
    }

    protected override void EmitDecrementVar(string varName, TargetWriter wr) {
      wr.Indent();
      wr.WriteLine("{0}--;", varName);
    }

    protected override string GetQuantifierName(string bvType) {
      return string.Format("Dafny.Helpers.Quantifier<{0}>", bvType);
    }

    protected override BlockTargetWriter CreateForeachLoop(string boundVar, out TargetWriter collectionWriter, TargetWriter wr, string/*?*/ altBoundVarName = null, Type/*?*/ altVarType = null, Bpl.IToken/*?*/ tok = null) {
      wr.Indent();
      wr.Write("foreach (var {0} in ", boundVar);
      collectionWriter = new TargetWriter(wr.IndentLevel);
      wr.Append(collectionWriter);
      if (altBoundVarName == null) {
        return wr.NewBlock(")");
      } else if (altVarType == null) {
        return wr.NewBlockWithPrefix(")", "{0} = {1};", altBoundVarName, boundVar);
      } else {
        return wr.NewBlockWithPrefix(")", "{2} {0} = ({2}){1};", altBoundVarName, boundVar, TypeName(altVarType, wr, tok));
      }
    }

    // ----- Expressions -------------------------------------------------------------

    protected override void EmitNew(Type type, Bpl.IToken tok, CallStmt/*?*/ initCall, TargetWriter wr) {
      var ctor = initCall == null ? null : (Constructor)initCall.Method;  // correctness of cast follows from precondition of "EmitNew"
      wr.Write("new {0}(", TypeName(type, wr, tok));
      string q, n;
      if (ctor != null && ctor.IsExtern(out q, out n)) {
        // the arguments of any external constructor are placed here
        string sep = "";
        for (int i = 0; i < ctor.Ins.Count; i++) {
          Formal p = ctor.Ins[i];
          if (!p.IsGhost) {
            wr.Write(sep);
            TrExpr(initCall.Args[i], wr, false);
            sep = ", ";
          }
        }
      }
      wr.Write(")");
    }

    protected override void EmitNewArray(Type elmtType, Bpl.IToken tok, List<Expression> dimensions, bool mustInitialize, TargetWriter wr) {
      if (!mustInitialize || HasSimpleZeroInitializer(elmtType)) {
        string typeNameSansBrackets, brackets;
        TypeName_SplitArrayName(elmtType, wr, tok, out typeNameSansBrackets, out brackets);
        wr.Write("new {0}", typeNameSansBrackets);
        string prefix = "[";
        foreach (var dim in dimensions) {
          wr.Write("{0}(int)", prefix);
          TrParenExpr(dim, wr, false);
          prefix = ", ";
        }
        wr.Write("]{0}", brackets);
      } else {
        wr.Write("Dafny.ArrayHelpers.InitNewArray{0}<{1}>", dimensions.Count, TypeName(elmtType, wr, tok));
        wr.Write("(");
        wr.Write(DefaultValue(elmtType, wr, tok));
        foreach (var dim in dimensions) {
          wr.Write(", ");
          TrParenExpr(dim, wr, false);
        }
        wr.Write(")");
      }
    }

    protected override void EmitLiteralExpr(TextWriter wr, LiteralExpr e) {
      if (e is StaticReceiverExpr) {
        wr.Write(TypeName(e.Type, wr, e.tok));
      } else if (e.Value == null) {
        var cl = (e.Type.NormalizeExpand() as UserDefinedType)?.ResolvedClass;
        bool isHandle = true;
        if (cl != null && Attributes.ContainsBool(cl.Attributes, "handle", ref isHandle) && isHandle) {
          wr.Write("0");
        } else {
          wr.Write("({0})null", TypeName(e.Type, wr, e.tok));
        }
      } else if (e.Value is bool) {
        wr.Write((bool)e.Value ? "true" : "false");
      } else if (e is CharLiteralExpr) {
        wr.Write("'{0}'", (string)e.Value);
      } else if (e is StringLiteralExpr) {
        var str = (StringLiteralExpr)e;
        wr.Write("{0}<char>.FromString(", DafnySeqClass);
        TrStringLiteral(str, wr);
        wr.Write(")");
      } else if (AsNativeType(e.Type) != null) {
        string nativeName = null, literalSuffix = null;
        bool needsCastAfterArithmetic = false;
        GetNativeInfo(AsNativeType(e.Type).Sel, out nativeName, out literalSuffix, out needsCastAfterArithmetic);
        wr.Write((BigInteger)e.Value + literalSuffix);
      } else if (e.Value is BigInteger) {
        var i = (BigInteger)e.Value;
        EmitIntegerLiteral(i, wr);
      } else if (e.Value is Basetypes.BigDec) {
        var n = (Basetypes.BigDec)e.Value;
        if (0 <= n.Exponent) {
          wr.Write("new Dafny.BigRational(BigInteger.Parse(\"{0}", n.Mantissa);
          for (int i = 0; i < n.Exponent; i++) {
            wr.Write("0");
          }
          wr.Write("\"), BigInteger.One)");
        } else {
          wr.Write("new Dafny.BigRational(");
          EmitIntegerLiteral(n.Mantissa, wr);
          wr.Write(", BigInteger.Parse(\"1");
          for (int i = n.Exponent; i < 0; i++) {
            wr.Write("0");
          }
          wr.Write("\"))");
        }
      } else {
        Contract.Assert(false); throw new cce.UnreachableException();  // unexpected literal
      }
    }
    void EmitIntegerLiteral(BigInteger i, TextWriter wr) {
      Contract.Requires(wr != null);
      if (new BigInteger(int.MinValue) <= i && i <= new BigInteger(int.MaxValue)) {
        wr.Write("new BigInteger({0})", i);
      } else {
        wr.Write("BigInteger.Parse(\"{0}\")", i);
      }
    }

    protected override void EmitStringLiteral(string str, bool isVerbatim, TextWriter wr) {
      wr.Write("{0}\"{1}\"", isVerbatim ? "@" : "", str);
    }

    protected override string IdProtect(string name) {
      return "@" + name;
    }

    protected override void EmitThis(TargetWriter wr) {
      wr.Write(enclosingMethod != null && enclosingMethod.IsTailRecursive ? "_this" : "this");
    }

    protected override void EmitDatatypeValue(DatatypeValue dtv, string dtName, string ctorName, string arguments, TargetWriter wr) {
      var typeParams = dtv.InferredTypeArgs.Count == 0 ? "" : string.Format("<{0}>", TypeNames(dtv.InferredTypeArgs, wr, dtv.tok));
      wr.Write("new @{0}{1}(", dtName, typeParams);
      if (!dtv.IsCoCall) {
        // For an ordinary constructor (that is, one that does not guard any co-recursive calls), generate:
        //   new Dt_Cons<T>( args )
        wr.Write("new {0}({1})", DtCtorName(dtv.Ctor, dtv.InferredTypeArgs, wr), arguments);
      } else {
        // In the case of a co-recursive call, generate:
        //     new Dt__Lazy<T>( LAMBDA )
        // where LAMBDA is:
        //     () => { return Dt_Cons<T>( ...args... ); }
        wr.Write("new {0}__Lazy{1}(", dtv.DatatypeName, typeParams);

        wr.Write("() => { return ");
        wr.Write("new {0}({1})", DtCtorName(dtv.Ctor, dtv.InferredTypeArgs, wr), arguments);
        wr.Write("; })");
      }
      wr.Write(")");
    }

    protected override void GetSpecialFieldInfo(SpecialField.ID id, object idParam, out string compiledName, out string preString, out string postString) {
      compiledName = "";
      preString = "";
      postString = "";
      switch (id) {
        case SpecialField.ID.UseIdParam:
          compiledName = (string)idParam;
          break;
        case SpecialField.ID.ArrayLength:
        case SpecialField.ID.ArrayLengthInt:
          compiledName = idParam == null ? "Length" : "GetLength(" + (int)idParam + ")";
          if (id == SpecialField.ID.ArrayLength) {
            preString = "new BigInteger(";
            postString = ")";
          }
          break;
        case SpecialField.ID.Floor:
          compiledName = "ToBigInteger()";
          break;
        case SpecialField.ID.IsLimit:
          preString = "Dafny.Helpers.BigOrdinal_IsLimit(";
          postString = ")";
          break;
        case SpecialField.ID.IsSucc:
          preString = "Dafny.Helpers.BigOrdinal_IsSucc(";
          postString = ")";
          break;
        case SpecialField.ID.Offset:
          preString = "Dafny.Helpers.BigOrdinal_Offset(";
          postString = ")";
          break;
        case SpecialField.ID.IsNat:
          preString = "Dafny.Helpers.BigOrdinal_IsNat(";
          postString = ")";
          break;
        case SpecialField.ID.Keys:
          compiledName = "Keys";
          break;
        case SpecialField.ID.Values:
          compiledName = "Values";
          break;
        case SpecialField.ID.Items:
          compiledName = "Items";
          break;
        case SpecialField.ID.Reads:
          compiledName = "_reads";
          break;
        case SpecialField.ID.Modifies:
          compiledName = "_modifies";
          break;
        case SpecialField.ID.New:
          compiledName = "_new";
          break;
        default:
          Contract.Assert(false); // unexpected ID
          break;
      }
    }

    protected override void EmitMemberSelect(MemberDecl member, bool isLValue, TargetWriter wr) {
      if (isLValue && member is ConstantField) {
        wr.Write("._{0}", member.CompileName);
      } else if (!isLValue && member is SpecialField sf) {
        string compiledName, preStr, postStr;
        GetSpecialFieldInfo(sf.SpecialId, sf.IdParam, out compiledName, out preStr, out postStr);
        if (compiledName.Length != 0) {
          wr.Write(".{0}", compiledName);
        } else {
          // this member selection is handled by some kind of enclosing function call, so nothing to do here
        }
      } else {
        wr.Write(".{0}", IdName(member));
      }
    }

    protected override void EmitArraySelect(List<string> indices, TargetWriter wr) {
      Contract.Assert(indices != null && 1 <= indices.Count);  // follows from precondition
      wr.Write("[");
      var sep = "";
      foreach (var index in indices) {
        wr.Write("{0}(int)({1})", sep, index);
        sep = ", ";
      }
      wr.Write("]");
    }

    protected override void EmitArraySelect(List<Expression> indices, bool inLetExprBody, TargetWriter wr) {
      Contract.Assert(indices != null && 1 <= indices.Count);  // follows from precondition
      wr.Write("[");
      var sep = "";
      foreach (var index in indices) {
        wr.Write("{0}(int)", sep);
        TrParenExpr(index, wr, inLetExprBody);
        sep = ", ";
      }
      wr.Write("]");
    }

    protected override void EmitIndexCollectionSelect(Expression source, Expression index, bool inLetExprBody, TargetWriter wr) {
      TrParenExpr(source, wr, inLetExprBody);
      TrParenExpr(".Select", index, wr, inLetExprBody);
    }

    protected override void EmitIndexCollectionUpdate(Expression source, Expression index, Expression value, bool inLetExprBody, TargetWriter wr) {
      TrParenExpr(source, wr, inLetExprBody);
      wr.Write(".Update(");
      TrExpr(index, wr, inLetExprBody);
      wr.Write(", ");
      TrExpr(value, wr, inLetExprBody);
      wr.Write(")");
    }

    protected override void EmitSeqSelectRange(Expression source, Expression/*?*/ lo, Expression/*?*/ hi, bool fromArray, bool inLetExprBody, TargetWriter wr) {
      if (fromArray) {
        wr.Write("Dafny.Helpers.SeqFromArray");
      }
      TrParenExpr(source, wr, inLetExprBody);
      if (hi != null) {
        TrParenExpr(".Take", hi, wr, inLetExprBody);
      }
      if (lo != null) {
        TrParenExpr(".Drop", lo, wr, inLetExprBody);
      }
    }

    protected override void EmitApplyExpr(Type functionType, Bpl.IToken tok, Expression function, List<Expression> arguments, bool inLetExprBody, TargetWriter wr) {
      wr.Write("Dafny.Helpers.Id<");
      wr.Write(TypeName(functionType, wr, tok));
      wr.Write(">(");
      TrExpr(function, wr, inLetExprBody);
      wr.Write(")");
      TrExprList(arguments, wr, inLetExprBody);
    }

    protected override TargetWriter EmitBetaRedex(string boundVars, List<Expression> arguments, string typeArgs, bool inLetExprBody, TargetWriter wr) {
      wr.Write("Dafny.Helpers.Id<{0}>(({1}) => ", typeArgs, boundVars);
      var w = new TargetWriter(wr.IndentLevel);
      wr.Append(w);
      wr.Write(")");
      TrExprList(arguments, wr, inLetExprBody);
      return w;
    }

    protected override void EmitDestructor(string source, Formal dtor, int formalNonGhostIndex, DatatypeCtor ctor, List<Type> typeArgs, TargetWriter wr) {
      var dtorName = FormalName(dtor, formalNonGhostIndex);
      wr.Write("(({0}){1}._D).{2}", DtCtorName(ctor, typeArgs, wr), source, dtorName);
    }

    protected override BlockTargetWriter CreateLambda(List<Type> inTypes, Bpl.IToken tok, List<string> inNames, Type resultType, TargetWriter wr) {
      // (
      //   (System.Func<inTypes,resultType>)  // cast, which tells C# what the various types involved are
      //   (
      //     (inNames) => {
      //       <<caller fills in body here; must end with a return statement>>
      //     }
      //   )
      // )
      wr.Write("((System.Func<{0}{1}>)(({2}) =>",
        Util.Comma("", inTypes, t => TypeName(t, wr, tok) + ", "),
        TypeName(resultType, wr, tok),
        Util.Comma(inNames, nm => nm));
      var w = wr.NewBlock("", "))");
      w.SetBraceStyle(BlockTargetWriter.BraceStyle.Space, BlockTargetWriter.BraceStyle.Nothing);
      return w;
    }

    protected override TargetWriter CreateIIFE_ExprBody(Expression source, bool inLetExprBody, Type sourceType, Bpl.IToken sourceTok, Type resultType, Bpl.IToken resultTok, string bvName, TargetWriter wr) {
      wr.Write("Dafny.Helpers.Let<{0},{1}>(", TypeName(sourceType, wr, sourceTok), TypeName(resultType, wr, resultTok));
      TrExpr(source, wr, inLetExprBody);
      wr.Write(", {0} => ", bvName);
      var w = new TargetWriter(wr.IndentLevel);
      wr.Append(w);
      wr.Write(")");
      int y = ((System.Func<int,int>)((u) => u + 5))(6);
      return w;
    }

    protected override TargetWriter CreateIIFE_ExprBody(string source, Type sourceType, Bpl.IToken sourceTok, Type resultType, Bpl.IToken resultTok, string bvName, TargetWriter wr) {
      wr.Write("Dafny.Helpers.Let<{0},{1}>(", TypeName(sourceType, wr, sourceTok), TypeName(resultType, wr, resultTok));
      wr.Write("{0}, {1} => ", source, bvName);
      var w = new TargetWriter(wr.IndentLevel);
      wr.Append(w);
      wr.Write(")");
      return w;
    }

    protected override BlockTargetWriter CreateIIFE0(Type resultType, Bpl.IToken resultTok, TargetWriter wr) {
      // (
      //   (System.Func<resultType>)(() => <<body>>)
      // )()
      wr.Write("((System.Func<{0}>)(() =>", TypeName(resultType, wr, resultTok));
      var w = wr.NewBlock("", "))()");
      w.SetBraceStyle(BlockTargetWriter.BraceStyle.Space, BlockTargetWriter.BraceStyle.Nothing);
      return w;
    }

    protected override BlockTargetWriter CreateIIFE1(int source, Type resultType, Bpl.IToken resultTok, string bvName, TargetWriter wr) {
      wr.Write("Dafny.Helpers.Let<int,{0}>(", TypeName(resultType, wr, resultTok));
      wr.Write("{0}, {1} => ", source, bvName);
      var w = wr.NewBlock("", ")");
      w.SetBraceStyle(BlockTargetWriter.BraceStyle.Space, BlockTargetWriter.BraceStyle.Nothing);
      return w;
    }

    protected override void EmitUnaryExpr(ResolvedUnaryOp op, Expression expr, bool inLetExprBody, TargetWriter wr) {
      switch (op) {
        case ResolvedUnaryOp.BoolNot:
          TrParenExpr("!", expr, wr, inLetExprBody);
          break;
        case ResolvedUnaryOp.BitwiseNot:
          TrParenExpr("~", expr, wr, inLetExprBody);
          break;
        case ResolvedUnaryOp.Cardinality:
          TrParenExpr("new BigInteger(", expr, wr, inLetExprBody);
          wr.Write(".Count)");
          break;
        default:
          Contract.Assert(false); throw new cce.UnreachableException();  // unexpected unary expression
      }
    }

    static bool IsDirectlyComparable(Type t) {
      Contract.Requires(t != null);
      return t.IsBoolType || t.IsCharType || t.IsIntegerType || t.IsRealType || t.AsNewtype != null || t.IsBitVectorType || t.IsBigOrdinalType || t.IsRefType;
    }

    protected override void CompileBinOp(BinaryExpr.ResolvedOpcode op,
      Expression e0, Expression e1, Bpl.IToken tok, Type resultType,
      out string opString,
      out string preOpString,
      out string postOpString,
      out string callString,
      out string staticCallString,
      out bool reverseArguments,
      out bool truncateResult,
      out bool convertE1_to_int,
      TextWriter errorWr) {

      opString = null;
      preOpString = "";
      postOpString = "";
      callString = null;
      staticCallString = null;
      reverseArguments = false;
      truncateResult = false;
      convertE1_to_int = false;

      switch (op) {
        case BinaryExpr.ResolvedOpcode.Iff:
          opString = "=="; break;
        case BinaryExpr.ResolvedOpcode.Imp:
          preOpString = "!"; opString = "||"; break;
        case BinaryExpr.ResolvedOpcode.Or:
          opString = "||"; break;
        case BinaryExpr.ResolvedOpcode.And:
          opString = "&&"; break;
        case BinaryExpr.ResolvedOpcode.BitwiseAnd:
          opString = "&"; break;
        case BinaryExpr.ResolvedOpcode.BitwiseOr:
          opString = "|"; break;
        case BinaryExpr.ResolvedOpcode.BitwiseXor:
          opString = "^"; break;

        case BinaryExpr.ResolvedOpcode.EqCommon: {
            if (IsHandleComparison(tok, e0, e1, errorWr)) {
              opString = "==";
            } else if (e0.Type.IsRefType) {
              // Dafny's type rules are slightly different C#, so we may need a cast here.
              // For example, Dafny allows x==y if x:array<T> and y:array<int> and T is some
              // type parameter.
              opString = "== (object)";
            } else if (IsDirectlyComparable(e0.Type)) {
              opString = "==";
            } else {
              callString = "Equals";
            }
            break;
          }
        case BinaryExpr.ResolvedOpcode.NeqCommon: {
            if (IsHandleComparison(tok, e0, e1, errorWr)) {
              opString = "!=";
            } else if (e0.Type.IsRefType) {
              // Dafny's type rules are slightly different C#, so we may need a cast here.
              // For example, Dafny allows x==y if x:array<T> and y:array<int> and T is some
              // type parameter.
              opString = "!= (object)";
            } else if (IsDirectlyComparable(e0.Type)) {
              opString = "!=";
            } else {
              preOpString = "!";
              callString = "Equals";
            }
            break;
          }

        case BinaryExpr.ResolvedOpcode.Lt:
        case BinaryExpr.ResolvedOpcode.LtChar:
          opString = "<"; break;
        case BinaryExpr.ResolvedOpcode.Le:
        case BinaryExpr.ResolvedOpcode.LeChar:
          opString = "<="; break;
        case BinaryExpr.ResolvedOpcode.Ge:
        case BinaryExpr.ResolvedOpcode.GeChar:
          opString = ">="; break;
        case BinaryExpr.ResolvedOpcode.Gt:
        case BinaryExpr.ResolvedOpcode.GtChar:
          opString = ">"; break;
        case BinaryExpr.ResolvedOpcode.LeftShift:
          opString = "<<"; truncateResult = true; convertE1_to_int = true; break;
        case BinaryExpr.ResolvedOpcode.RightShift:
          opString = ">>"; convertE1_to_int = true; break;
        case BinaryExpr.ResolvedOpcode.Add:
          opString = "+"; truncateResult = true;
          if (resultType.IsCharType) {
            preOpString = "(char)(";
            postOpString = ")";
          }
          break;
        case BinaryExpr.ResolvedOpcode.Sub:
          opString = "-"; truncateResult = true;
          if (resultType.IsCharType) {
            preOpString = "(char)(";
            postOpString = ")";
          }
          break;
        case BinaryExpr.ResolvedOpcode.Mul:
          opString = "*"; truncateResult = true; break;
        case BinaryExpr.ResolvedOpcode.Div:
          if (resultType.IsIntegerType || (AsNativeType(resultType) != null && AsNativeType(resultType).LowerBound < BigInteger.Zero)) {
            var suffix = AsNativeType(resultType) != null ? "_" + GetNativeTypeName(AsNativeType(resultType)) : "";
            staticCallString = "Dafny.Helpers.EuclideanDivision" + suffix;
          } else {
            opString = "/";  // for reals
          }
          break;
        case BinaryExpr.ResolvedOpcode.Mod:
          if (resultType.IsIntegerType || (AsNativeType(resultType) != null && AsNativeType(resultType).LowerBound < BigInteger.Zero)) {
            var suffix = AsNativeType(resultType) != null ? "_" + GetNativeTypeName(AsNativeType(resultType)) : "";
            staticCallString = "Dafny.Helpers.EuclideanModulus" + suffix;
          } else {
            opString = "%";  // for reals
          }
          break;
        case BinaryExpr.ResolvedOpcode.SetEq:
        case BinaryExpr.ResolvedOpcode.MultiSetEq:
        case BinaryExpr.ResolvedOpcode.SeqEq:
        case BinaryExpr.ResolvedOpcode.MapEq:
          callString = "Equals"; break;
        case BinaryExpr.ResolvedOpcode.SetNeq:
        case BinaryExpr.ResolvedOpcode.MultiSetNeq:
        case BinaryExpr.ResolvedOpcode.SeqNeq:
        case BinaryExpr.ResolvedOpcode.MapNeq:
          preOpString = "!"; callString = "Equals"; break;
        case BinaryExpr.ResolvedOpcode.ProperSubset:
        case BinaryExpr.ResolvedOpcode.ProperMultiSubset:
          callString = "IsProperSubsetOf"; break;
        case BinaryExpr.ResolvedOpcode.Subset:
        case BinaryExpr.ResolvedOpcode.MultiSubset:
          callString = "IsSubsetOf"; break;
        case BinaryExpr.ResolvedOpcode.Superset:
        case BinaryExpr.ResolvedOpcode.MultiSuperset:
          callString = "IsSupersetOf"; break;
        case BinaryExpr.ResolvedOpcode.ProperSuperset:
        case BinaryExpr.ResolvedOpcode.ProperMultiSuperset:
          callString = "IsProperSupersetOf"; break;
        case BinaryExpr.ResolvedOpcode.Disjoint:
        case BinaryExpr.ResolvedOpcode.MultiSetDisjoint:
        case BinaryExpr.ResolvedOpcode.MapDisjoint:
          callString = "IsDisjointFrom"; break;
        case BinaryExpr.ResolvedOpcode.InSet:
        case BinaryExpr.ResolvedOpcode.InMultiSet:
        case BinaryExpr.ResolvedOpcode.InMap:
          callString = "Contains"; reverseArguments = true; break;
        case BinaryExpr.ResolvedOpcode.NotInSet:
        case BinaryExpr.ResolvedOpcode.NotInMultiSet:
        case BinaryExpr.ResolvedOpcode.NotInMap:
          preOpString = "!"; callString = "Contains"; reverseArguments = true; break;
        case BinaryExpr.ResolvedOpcode.Union:
        case BinaryExpr.ResolvedOpcode.MultiSetUnion:
          callString = "Union"; break;
        case BinaryExpr.ResolvedOpcode.Intersection:
        case BinaryExpr.ResolvedOpcode.MultiSetIntersection:
          callString = "Intersect"; break;
        case BinaryExpr.ResolvedOpcode.SetDifference:
        case BinaryExpr.ResolvedOpcode.MultiSetDifference:
          callString = "Difference"; break;

        case BinaryExpr.ResolvedOpcode.ProperPrefix:
          callString = "IsProperPrefixOf"; break;
        case BinaryExpr.ResolvedOpcode.Prefix:
          callString = "IsPrefixOf"; break;
        case BinaryExpr.ResolvedOpcode.Concat:
          callString = "Concat"; break;
        case BinaryExpr.ResolvedOpcode.InSeq:
          callString = "Contains"; reverseArguments = true; break;
        case BinaryExpr.ResolvedOpcode.NotInSeq:
          preOpString = "!"; callString = "Contains"; reverseArguments = true; break;

        default:
          Contract.Assert(false); throw new cce.UnreachableException();  // unexpected binary expression
      }
    }

    protected override void EmitIsZero(string varName, TargetWriter wr) {
      wr.Write("{0} == 0", varName);
    }    

    protected override void EmitConversionExpr(ConversionExpr e, bool inLetExprBody, TargetWriter wr) {
      if (e.E.Type.IsNumericBased(Type.NumericPersuation.Int) || e.E.Type.IsBitVectorType || e.E.Type.IsCharType) {
        if (e.ToType.IsNumericBased(Type.NumericPersuation.Real)) {
          // (int or bv) -> real
          Contract.Assert(AsNativeType(e.ToType) == null);
          wr.Write("new Dafny.BigRational(");
          if (AsNativeType(e.E.Type) != null) {
            wr.Write("new BigInteger");
          }
          TrParenExpr(e.E, wr, inLetExprBody);
          wr.Write(", BigInteger.One)");
        } else if (e.ToType.IsCharType) {
          wr.Write("(char)(");
          TrExpr(e.E, wr, inLetExprBody);
          wr.Write(")");
        } else {
          // (int or bv) -> (int or bv or ORDINAL)
          var fromNative = AsNativeType(e.E.Type);
          var toNative = AsNativeType(e.ToType);
          if (fromNative == null && toNative == null) {
            // big-integer (int or bv) -> big-integer (int or bv or ORDINAL), so identity will do
            TrExpr(e.E, wr, inLetExprBody);
          } else if (fromNative != null && toNative == null) {
            // native (int or bv) -> big-integer (int or bv)
            wr.Write("new BigInteger");
            TrParenExpr(e.E, wr, inLetExprBody);
          } else {
            string toNativeName, toNativeSuffix;
            bool toNativeNeedsCast;
            GetNativeInfo(toNative.Sel, out toNativeName, out toNativeSuffix, out toNativeNeedsCast);
            // any (int or bv) -> native (int or bv)
            // A cast would do, but we also consider some optimizations
            wr.Write("({0})", toNativeName);

            var literal = PartiallyEvaluate(e.E);
            UnaryOpExpr u = e.E.Resolved as UnaryOpExpr;
            MemberSelectExpr m = e.E.Resolved as MemberSelectExpr;
            if (literal != null) {
              // Optimize constant to avoid intermediate BigInteger
              wr.Write("(" + literal + toNativeSuffix + ")");
            } else if (u != null && u.Op == UnaryOpExpr.Opcode.Cardinality) {
              // Optimize .Count to avoid intermediate BigInteger
              TrParenExpr(u.E, wr, inLetExprBody);
              if (toNative.UpperBound <= new BigInteger(0x80000000U)) {
                wr.Write(".Count");
              } else {
                wr.Write(".LongCount");
              }
            } else if (m != null && m.MemberName == "Length" && m.Obj.Type.IsArrayType) {
              // Optimize .Length to avoid intermediate BigInteger
              TrParenExpr(m.Obj, wr, inLetExprBody);
              if (toNative.UpperBound <= new BigInteger(0x80000000U)) {
                wr.Write(".Length");
              } else {
                wr.Write(".LongLength");
              }
            } else {
              // no optimization applies; use the standard translation
              TrParenExpr(e.E, wr, inLetExprBody);
            }

          }
        }
      } else if (e.E.Type.IsNumericBased(Type.NumericPersuation.Real)) {
        Contract.Assert(AsNativeType(e.E.Type) == null);
        if (e.ToType.IsNumericBased(Type.NumericPersuation.Real)) {
          // real -> real
          Contract.Assert(AsNativeType(e.ToType) == null);
          TrExpr(e.E, wr, inLetExprBody);
        } else {
          // real -> (int or bv)
          if (AsNativeType(e.ToType) != null) {
            wr.Write("({0})", GetNativeTypeName(AsNativeType(e.ToType)));
          }
          TrParenExpr(e.E, wr, inLetExprBody);
          wr.Write(".ToBigInteger()");
        }
      } else {
        Contract.Assert(e.E.Type.IsBigOrdinalType);
        Contract.Assert(e.ToType.IsNumericBased(Type.NumericPersuation.Int));
        // identity will do
        TrExpr(e.E, wr, inLetExprBody);
      }
    }

    protected override void EmitCollectionDisplay(CollectionType ct, Bpl.IToken tok, List<Expression> elements, bool inLetExprBody, TargetWriter wr) {
      wr.Write("{0}.FromElements", TypeName(ct, wr, tok));
      TrExprList(elements, wr, inLetExprBody);
    }

    protected override void EmitMapDisplay(MapType mt, Bpl.IToken tok, List<ExpressionPair> elements, bool inLetExprBody, TargetWriter wr) {
      wr.Write("{0}.FromElements", TypeName(mt, wr, tok));
      wr.Write("(");
      string sep = "";
      foreach (ExpressionPair p in elements) {
        wr.Write(sep);
        wr.Write("new Dafny.Pair<");
        wr.Write(TypeName(p.A.Type, wr, p.A.tok));
        wr.Write(",");
        wr.Write(TypeName(p.B.Type, wr, p.B.tok));
        wr.Write(">(");
        TrExpr(p.A, wr, inLetExprBody);
        wr.Write(",");
        TrExpr(p.B, wr, inLetExprBody);
        wr.Write(")");
        sep = ", ";
      }
      wr.Write(")");
    }

    protected override void EmitCollectionBuilder_New(CollectionType ct, Bpl.IToken tok, TargetWriter wr) {
      if (ct is SetType) {
        wr.Write("new System.Collections.Generic.List<{0}>()", TypeName(ct.Arg, wr, tok));
      } else if (ct is MapType) {
        var mt = (MapType)ct;
        var domtypeName = TypeName(mt.Domain, wr, tok);
        var rantypeName = TypeName(mt.Range, wr, tok);
        wr.Write("new System.Collections.Generic.List<Dafny.Pair<{0},{1}>>()", domtypeName, rantypeName);
      } else {
        Contract.Assume(false);  // unepxected collection type
      }
    }

    protected override void EmitCollectionBuilder_Add(CollectionType ct, string collName, Expression elmt, bool inLetExprBody, TargetWriter wr) {
      if (ct is SetType) {
        wr.Indent();
        wr.Write("{0}.Add(", collName);
        TrExpr(elmt, wr, inLetExprBody);
        wr.WriteLine(");");
      } else {
        Contract.Assume(false);  // unepxected collection type
      }
    }

    protected override TargetWriter EmitMapBuilder_Add(MapType mt, Bpl.IToken tok, string collName, Expression term, bool inLetExprBody, TargetWriter wr) {
      var domtypeName = TypeName(mt.Domain, wr, tok);
      var rantypeName = TypeName(mt.Range, wr, tok);
      wr.Indent();
      wr.Write("{0}.Add(new Dafny.Pair<{1},{2}>(", collName, domtypeName, rantypeName);
      var termLeftWriter = new TargetWriter(wr.IndentLevel);
      wr.Append(termLeftWriter);
      wr.Write(",");
      TrExpr(term, wr, inLetExprBody);
      wr.WriteLine("));");
      return termLeftWriter;
    }

    protected override string GetCollectionBuilder_Build(CollectionType ct, Bpl.IToken tok, string collName, TargetWriter wr) {
      if (ct is SetType) {
        var typeName = TypeName(ct.Arg, wr, tok);
        return string.Format("Dafny.Set<{0}>.FromCollection({1})", typeName, collName);
      } else if (ct is MapType) {
        var mt = (MapType)ct;
        var domtypeName = TypeName(mt.Domain, wr, tok);
        var rantypeName = TypeName(mt.Range, wr, tok);
        return string.Format("{3}<{0},{1}>.FromCollection({2})", domtypeName, rantypeName, collName, DafnyMapClass);
      } else {
        Contract.Assume(false);  // unepxected collection type
        throw new cce.UnreachableException();  // please compiler
      }
    }

    protected override void EmitSingleValueGenerator(Expression e, bool inLetExprBody, string type, TargetWriter wr) {
      wr.Write("Dafny.Helpers.SingleValue<{0}>", type);
      TrParenExpr(e, wr, inLetExprBody);
    }

    // ----- Target compilation and execution -------------------------------------------------------------

    private class CSharpCompilationResult
    {
      public string libPath;
      public List<string> immutableDllFileNames;
      public CompilerResults cr;
    }

    public override bool CompileTargetProgram(string dafnyProgramName, string targetProgramText, string/*?*/ callToMain, string/*?*/ targetFilename, ReadOnlyCollection<string> otherFileNames,
      bool hasMain, bool runAfterCompile, TextWriter outputWriter, out object compilationResult) {

      compilationResult = null;

      if (!CodeDomProvider.IsDefinedLanguage("CSharp")) {
        outputWriter.WriteLine("Error: cannot compile, because there is no provider configured for input language CSharp");
        return false;
      }

      var provider = CodeDomProvider.CreateProvider("CSharp", new Dictionary<string, string> { { "CompilerVersion", "v4.0" } });
      var cp = new System.CodeDom.Compiler.CompilerParameters();
      cp.GenerateExecutable = hasMain;
      if (DafnyOptions.O.RunAfterCompile) {
        cp.GenerateInMemory = true;
      } else if (hasMain) {
        cp.OutputAssembly = Path.ChangeExtension(dafnyProgramName, "exe");
        cp.GenerateInMemory = false;
      } else {
        cp.OutputAssembly = Path.ChangeExtension(dafnyProgramName, "dll");
        cp.GenerateInMemory = false;
      }
      // The nowarn numbers are the following:
      // * CS0164 complains about unreferenced labels
      // * CS0219/CS0168 is about unused variables
      // * CS1717 is about assignments of a variable to itself
      // * CS0162 is about unreachable code
      cp.CompilerOptions = "/debug /nowarn:0164 /nowarn:0219 /nowarn:1717 /nowarn:0162 /nowarn:0168";
      cp.ReferencedAssemblies.Add("System.Numerics.dll");
      cp.ReferencedAssemblies.Add("System.Core.dll");
      cp.ReferencedAssemblies.Add("System.dll");

      var crx = new CSharpCompilationResult();
      crx.libPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) + Path.DirectorySeparatorChar;
      if (DafnyOptions.O.UseRuntimeLib) {
        cp.ReferencedAssemblies.Add(crx.libPath + "DafnyRuntime.dll");
      }

      crx.immutableDllFileNames = new List<string>() {
        "System.Collections.Immutable.dll",
        "System.Runtime.dll",
        "netstandard.dll"
      };

      if (DafnyOptions.O.Optimize) {
        cp.CompilerOptions += " /optimize /define:DAFNY_USE_SYSTEM_COLLECTIONS_IMMUTABLE";
        cp.CompilerOptions += " /lib:" + crx.libPath;
        foreach (var filename in crx.immutableDllFileNames) {
          cp.ReferencedAssemblies.Add(filename);
        }
      }

      int numOtherSourceFiles = 0;
      if (otherFileNames.Count > 0) {
        foreach (var file in otherFileNames) {
          string extension = Path.GetExtension(file);
          if (extension != null) { extension = extension.ToLower(); }
          if (extension == ".cs") {
            numOtherSourceFiles++;
          } else if (extension == ".dll") {
            cp.ReferencedAssemblies.Add(file);
          }
        }
      }

      if (numOtherSourceFiles > 0) {
        string[] sourceFiles = new string[numOtherSourceFiles + 1];
        sourceFiles[0] = targetFilename;
        int index = 1;
        foreach (var file in otherFileNames) {
          string extension = Path.GetExtension(file);
          if (extension != null) { extension = extension.ToLower(); }
          if (extension == ".cs") {
            sourceFiles[index++] = file;
          }
        }
        crx.cr = provider.CompileAssemblyFromFile(cp, sourceFiles);
      } else {
        crx.cr = provider.CompileAssemblyFromSource(cp, targetProgramText);
      }

      if (crx.cr.Errors.Count != 0) {
        if (cp.GenerateInMemory) {
          outputWriter.WriteLine("Errors compiling program");
        } else {
          var assemblyName = Path.GetFileName(crx.cr.PathToAssembly);
          outputWriter.WriteLine("Errors compiling program into {0}", assemblyName);
        }
        foreach (var ce in crx.cr.Errors) {
          outputWriter.WriteLine(ce.ToString());
          outputWriter.WriteLine();
        }
        return false;
      }

      if (!cp.GenerateInMemory) {
        var assemblyName = Path.GetFileName(crx.cr.PathToAssembly);
        outputWriter.WriteLine("Compiled assembly into {0}", assemblyName);
        if (DafnyOptions.O.Optimize) {
          var outputDir = Path.GetDirectoryName(dafnyProgramName);
          if (string.IsNullOrWhiteSpace(outputDir)) {
            outputDir = ".";
          }
          foreach (var filename in crx.immutableDllFileNames) {
            var destPath = outputDir + Path.DirectorySeparatorChar + filename;
            File.Copy(crx.libPath + filename, destPath, true);
            outputWriter.WriteLine("Copied /optimize dependency {0} to {1}", filename, outputDir);
          }
        }
      }

      compilationResult = crx;
      return true;
    }

    public override bool RunTargetProgram(string dafnyProgramName, string targetProgramText, string callToMain, string/*?*/ targetFilename, ReadOnlyCollection<string> otherFileNames,
      object compilationResult, TextWriter outputWriter) {

      var crx = (CSharpCompilationResult)compilationResult;
      var cr = crx.cr;

      var assemblyName = Path.GetFileName(cr.PathToAssembly);
      var entry = cr.CompiledAssembly.EntryPoint;
      try {
        object[] parameters = entry.GetParameters().Length == 0 ? new object[] { } : new object[] { new string[0] };
        entry.Invoke(null, parameters);
        return true;
      } catch (System.Reflection.TargetInvocationException e) {
        outputWriter.WriteLine("Error: Execution resulted in exception: {0}", e.Message);
        outputWriter.WriteLine(e.InnerException.ToString());
      } catch (System.Exception e) {
        outputWriter.WriteLine("Error: Execution resulted in exception: {0}", e.Message);
        outputWriter.WriteLine(e.ToString());
      }
      return false;
    }
  }
}