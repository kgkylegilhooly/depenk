namespace Depenk.Core.Model;

public enum Confidence { Low, Medium, High, Certain }
public enum ProjectKind { Api, Client, Library, Test, Other }
public enum ModelKind { Class, Record, Struct, Enum, Opaque }
public enum EdgeKind { References, Produces, Targets, Invokes, Accepts, Returns, FieldOf, DependsOn }
