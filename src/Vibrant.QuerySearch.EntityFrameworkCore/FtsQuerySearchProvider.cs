﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Vibrant.QuerySearch.Form;

namespace Vibrant.QuerySearch.EntityFrameworkCore
{
   /// <summary>
   /// Implementation if IQuerySearchProvider that supports simple fulltext search.
   /// </summary>
   /// <typeparam name="TEntity"></typeparam>
   public abstract class FtsQuerySearchProvider<TEntity> : DefaultQuerySearchProvider<TEntity>
      where TEntity : class
   {
      private static readonly char[] SplitChars = new[] { ' ', '\t', '\r', '\n' };

      private static readonly string TableAlias = "[ftst]";
      private static readonly string KeyTable = "[KEY_TBL]";

      /// <summary>
      /// Gets or sets the full text index search mode to use.
      /// </summary>
      public FtsSearchMode SearchMode { get; protected set; }

      /// <summary>
      /// Constructs an FtsQuerySearchProvider.
      /// </summary>
      public FtsQuerySearchProvider( ILocalizationService localization ) : base( localization )
      {
      }

      /// <summary>
      /// Gets the name of the table that this table is placed in. Example: [BlogPost]
      /// </summary>
      protected abstract string GetTableName();

      /// <summary>
      /// Gets the primary key column name. Example: [Id]
      /// </summary>
      /// <returns></returns>
      protected abstract string GetKeyColumnName();

      /// <summary>
      /// Gets unique sort for this table. Example [Id] ASC
      /// </summary>
      protected abstract string GetUniqueColumnSort();

      /// <summary>
      /// Gets the columns to perform the search on given the specified term.
      /// </summary>
      /// <param name="term"></param>
      /// <returns></returns>
      protected virtual string[] GetSearchColumns( string term )
      {
         return new[] { "*" };
      }

      private string GetSearchExpression( string term )
      {
         if( SearchMode == FtsSearchMode.FreeText )
         {
            return term;
         }
         else if( SearchMode == FtsSearchMode.WeightedPrefixes )
         {
            if( string.IsNullOrWhiteSpace( term ) )
            {
               return null;
            }

            var words = term.Split( SplitChars, StringSplitOptions.RemoveEmptyEntries );
            if( words.Length == 0 )
            {
               return null;
            }

            var maxLength = words.Max( x => x.Length );
            var isAbout = $"ISABOUT({string.Join( ", ", words.Select( x => "\"" + x.Replace( "\"", "" ) + "*\" WEIGHT (" + ( (double)x.Length / maxLength ).ToString( "0.##", CultureInfo.InvariantCulture ) + ")" ) )})";
            return isAbout;
         }
         else
         {
            if( string.IsNullOrWhiteSpace( term ) )
            {
               return null;
            }

            var words = term.Split( SplitChars, StringSplitOptions.RemoveEmptyEntries );
            if( words.Length == 0 )
            {
               return null;
            }
            var allWords = new string[ words.Length * 2 ];

            Array.Copy( words, allWords, words.Length );
            for( int i = 0 ; i < words.Length ; i++ )
            {
               var charArray = words[ i ].ToCharArray();
               Array.Reverse( charArray );
               allWords[ words.Length + i ] = new string( charArray );
            }

            var maxLength = allWords.Max( x => x.Length );
            var isAbout = $"ISABOUT({string.Join( ", ", allWords.Select( x => "\"" + x.Replace( "\"", "" ) + "*\" WEIGHT (" + ( (double)x.Length / maxLength ).ToString( "0.##", CultureInfo.InvariantCulture ) + ")" ) )})";
            return isAbout;
         }
      }

      private string GetSearchTable()
      {
         switch( SearchMode )
         {
            case FtsSearchMode.FreeText:
               return "FREETEXTTABLE";
            case FtsSearchMode.WeightedPrefixes:
            case FtsSearchMode.WeightedPrefixesPlusReverse:
               return "CONTAINSTABLE";
            default:
               throw new InvalidOperationException( "SearchMode is not set correctly." );
         }
      }

      private string CreateBaseQuery( string term )
      {
         return $"SELECT {TableAlias}.* FROM {GetTableName()} AS {TableAlias} INNER JOIN {GetSearchTable()}({GetTableName()}, ({string.Join( ", ", GetSearchColumns( term ) )}), {{0}} ) AS {KeyTable} ON {TableAlias}.{GetKeyColumnName()} = {KeyTable}.[KEY]";
      }

      private string CreateOrderBy( int offset, int fetch )
      {
         return $"ORDER BY {KeyTable}.RANK DESC, {TableAlias}.{GetUniqueColumnSort()} OFFSET {offset} ROWS FETCH NEXT {fetch} ROWS ONLY";
      }

      public override IQueryable<TEntity> ApplyWhere( IQueryable<TEntity> query, IFilterForm filteringForm )
      {
         var pageForm = filteringForm as IPageForm;
         if( pageForm == null )
         {
            throw new QuerySearchException( "The IFilterForm must also implement IPageFrom in order to support FtsQuerySearchProvider." );
         }

         // keep track of the untouched query
         var untouchedQuery = query;

         // we only need to perform this 'special' filtering in case there is a term
         var rawTerm = filteringForm.GetTerm();
         var term = GetSearchExpression( rawTerm );
         if( !string.IsNullOrWhiteSpace( term ) )
         {
            // rewrite the 'base query' to include a full text filter (will be placed in a sub query)
            string baseQuery = CreateBaseQuery( rawTerm );
            query = query.FromSql( baseQuery, term );

            // apply the default where clause onto the query (will be placed in an outer query)
            query = base.ApplyWhere( query, filteringForm );

            // get the sql representing the query
            var sql = query.ToSql();

            // create a reader for our sql, and a builder to build a new query
            var reader = new StringReader( sql );
            var builder = new StringBuilder();
            builder.AppendLine( baseQuery );

            // variable to keep track of the alias used for the table in the outer query
            string usedAlias = string.Empty;

            // variable to keep track of whether or not the sql line we are iterating should be part of the new sql statement
            bool iteratingRelevantSql = false;

            string line = null;
            while( ( line = reader.ReadLine() ) != null )
            {
               if( line.StartsWith( ") AS " ) )
               {
                  // this line marks the start of WHERE/ORDER BY/WHATEVER of the outer query
                  usedAlias = line.Substring( 5 );
                  iteratingRelevantSql = true;
               }
               else if( iteratingRelevantSql )
               {
                  // this writes the WHERE clause + other stuff
                  builder.AppendLine( line.Replace( usedAlias, TableAlias ) );
               }
            }

            // Use the untouched query and append our own homebrew where clause
            return untouchedQuery.FromSql( builder.ToString(), term );
         }
         else
         {
            // no special filtering to be performed
            return base.ApplyWhere( query, filteringForm );
         }
      }

      public override PaginationResult<TEntity> ApplyPagination( IQueryable<TEntity> query, IPageForm form )
      {
         var filteringForm = form as IFilterForm;
         if( filteringForm == null )
         {
            throw new QuerySearchException( "The IPageForm must also implement IFilterForm in order to support FtsQuerySearchProvider." );
         }

         var term = GetSearchExpression( filteringForm.GetTerm() );
         if( !string.IsNullOrWhiteSpace( term ) )
         {
            // keep track of the untouched query
            var untouchedQuery = query;

            // apply where clause again (so we the outer query will be 'complete', and can simply replace what comes after the SELECT of the inner query)
            query = base.ApplyWhere( query, filteringForm );

            // if we are not sorting by the term, we should simply use the default mechanism
            bool isAlreadySorted = false;
            if( !form.SortByTermRank() )
            {
               isAlreadySorted = true;

               var result = base.ApplyPagination( query, form );
               query = result.Query;
            }

            // get the sql of the query (WHERE + JOIN + ORDER BY, etc.)
            var sql = query.ToSql();

            // create a reader for our sql, and a builder to build a new query
            var reader = new StringReader( sql );
            var builder = new StringBuilder();

            // variable to keep track of the alias used for the table in the outer query
            string usedAlias = string.Empty;

            // variable to keep track of whether or not the sql line we are iterating should be part of the new sql statement
            bool iteratingRelevantSql = false;

            string line = null;
            while( ( line = reader.ReadLine() ) != null )
            {
               if( line == "FROM (" )
               {
                  // read our base query
                  var baseQuery = reader.ReadLine();
                  builder.AppendLine( baseQuery );

                  // this line marks the start of the sub query
               }
               else if( line.StartsWith( ") AS " ) )
               {
                  // this line marks the start of WHERE/ORDER BY/WHATEVER of the outer query
                  usedAlias = line.Substring( 5 );

                  iteratingRelevantSql = true;
               }
               else if( iteratingRelevantSql )
               {
                  builder.AppendLine( line );
               }
            }

            // if we have NOT already appended a OFFSET/FETCH query part, do that now
            if( !isAlreadySorted )
            {
               // calculate fetch and offset
               int fetch = 0;
               int offset = 0;

               var page = form.GetPage();
               var skip = form.GetSkip();
               var take = form.GetTake();
               if( Mode == PaginationMode.SkipAndTake )
               {
                  if( skip.HasValue )
                  {
                     offset = skip.Value;
                  }
                  int actualTake = MaxTake;
                  if( take.HasValue && take.Value < MaxTake )
                  {
                     fetch = MaxTake;
                  }
               }
               else
               {
                  var pageSize = GetPageSize( form );
                  int actualPage = 0;
                  if( page.HasValue )
                  {
                     actualPage = page.Value;
                  }
                  else if( skip.HasValue )
                  {
                     actualPage = skip.Value / pageSize;
                  }

                  offset = actualPage * pageSize;
                  fetch = pageSize;
               }

               // we have NOT ordered sql, which means that we need to do it based on the term
               builder.AppendLine( CreateOrderBy( offset, fetch ) );
            }

            // calculate the final sql and use it as the base on the untouched query
            var rawSql = builder.Replace( usedAlias, TableAlias ).ToString();

            // parameterize the final sql
            int parameterStart = rawSql.IndexOf( ", N'" ) + 2;
            int parameterEnd = rawSql.IndexOf( "' ) AS" ) + 1;
            string parameter = rawSql.Substring( parameterStart, parameterEnd - parameterStart );

            var finalSql = rawSql.Replace( parameter, "{0}" );
            return CreatePaginationResult( untouchedQuery.FromSql( finalSql, term ), form, false );
         }
         else
         {
            return base.ApplyPagination( query, form );
         }
      }
   }
}
